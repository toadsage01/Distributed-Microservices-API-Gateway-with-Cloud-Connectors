# Architecture Overview

## Layers and dependencies

```
┌─────────────────────────────────────────────────────────────────┐
│                    ApiGateway.Api (HTTP host)                   │
│   Middleware: Exception → Correlation → JWT → RateLimit →       │
│               SchemaValidation                                  │
│   Controllers: GatewayController, HealthController               │
│   Program.cs: layered DI + OpenAPI + OpenTelemetry               │
└────────┬────────────────────────────────────────────────────────┘
         │ depends on
┌────────▼────────────────────────────────────────────────────────┐
│                 ApiGateway.Application                          │
│   Sagas: SagaOrchestrator, ICompensatableStep, step impls       │
│   CQRS: ForwardPartnerRequestCommand, GetPartnerMetricsQuery    │
│   Contracts: GatewayRequest, GatewayResponse, PartnerRouteContext│
│   Infrastructure adapters: HttpUpstreamInvoker, JsonSchemaValidator│
└────────┬────────────────────────────────────────────────────────┘
         │ depends on
┌────────▼────────────────────────────────────────────────────────┐
│              ApiGateway.Infrastructure                           │
│   Persistence: SqlConnectionFactory, repositories               │
│   Audit: SyncAuditWriter, AsyncAuditBatchWriter, fallback queue│
│   Secrets: SecretsManagerClient, NullSecretsProvider             │
│   Migration: MigrationRunner                                     │
└────────┬────────────────────────────────────────────────────────┘
         │ depends on
┌────────▼────────────────────────────────────────────────────────┐
│                   ApiGateway.Domain                              │
│   Entities: PartnerConfig, SagaExecutionLog, SchemaVersion,     │
│             AuditLog_Security, AuditLog_Metrics, SecurityAlert  │
│   ValueObjects: PartnerId, CorrelationId, SagaState, tiers     │
└─────────────────────────────────────────────────────────────────┘
```

An additional cross-cutting module:
- `ApiGateway.Infrastructure.Anomaly` depends on `Domain` + `Infrastructure`, exposes
  `IAnomalyScorer` for the future LLM-backed swap and subscribes to the same
  `IAuditMetricsChannel` as the async audit batch writer.

## Request lifecycle

```
1. Client sends POST /v1/acme/orders with Authorization: Bearer <jwt>
   and optional Idempotency-Key header.

2. ExceptionHandlingMiddleware wraps the pipeline (catch-all).

3. CorrelationIdMiddleware reads X-Correlation-Id (or generates one)
   and publishes via AsyncLocal CorrelationContext.

4. JwtAuthMiddleware validates the bearer token. On failure writes
   AuditLog_Security (EventType=AUTH_FAILURE) and returns 401.
   Trigger on AuditLog_Security fires SecurityAlert if the partner
   has crossed the burst threshold.

5. RateLimitMiddleware resolves PartnerConfig, checks the per-partner
   sliding window. On breach writes AuditLog_Security (EventType=
   RATE_LIMIT_BREACHED) and returns 429.

6. SchemaValidationMiddleware fetches the partner's effective
   SchemaVersion (X-Schema-Version header or latest effective),
   buffers the request body, validates against the JSON schema.
   On rejection writes AuditLog_Security (EventType=
   SCHEMA_VALIDATION_REJECTED) and returns 422.

7. GatewayController builds a GatewayRequest and dispatches
   ForwardPartnerRequestCommand via MediatR.

8. ForwardPartnerRequestHandler:
   a. Resolves PartnerConfig (idempotency-key fast path if hit).
   b. Sets CorrelationContext.
   c. Constructs SagaContext, invokes SagaOrchestrator.ExecuteAsync.
   d. Caches 2xx response against IdempotencyKey (if provided).

9. SagaOrchestrator:
   a. For each registered ISagaStep (Transform → CallUpstream → Aggregate):
      - Insert SagaExecutionLog row (state=pending)
      - Update state=running
      - Invoke step.ExecuteAsync
      - On success: update state=completed with payload snapshot
      - On failure: update state=failed, walk executed steps in
        reverse invoking ICompensatableStep.CompensateAsync,
        return 502.
   b. Returns GatewayResponse with the aggregated body.

10. AsyncAuditWriter enqueues AuditLog_Metrics record (latency,
    status, payload size) to the Channel<T>. This is fire-and-forget
    on the request path.

11. GatewayController writes X-Correlation-Id response header and
    returns the response.

Out-of-band:
  - AsyncAuditBatchWriter drains the channel every 250ms / 500 records,
    bulk-inserts into AuditLog_Metrics via SqlBulkCopy.
  - AnomalyDetectionService subscribes to the same channel, scores
    each record via IAnomalyScorer, writes SecurityAlert on deviation.
  - AuditFallbackRecoveryService drains the local file queue every
    30s when the primary DB is unavailable.
  - SagaRecoveryService scans SagaExecutionLog every 1 min for stale
    non-terminal rows past the 5-min threshold, runs recovery via
    SagaOrchestrator.RecoverAsync.
```

## Failure modes

| Failure | Detection | Mitigation |
|---|---|---|
| JWT signing key rotation | Secrets Manager version | SecretsManagerClient cache TTL 10min; new key pulled on cache miss |
| Partner not found | 404 from PartnerConfigLoader | Operator onboarding flow publishes PartnerConfig row |
| Schema version not registered | 503 from SchemaValidationMiddleware | Operator publishes SchemaVersion row |
| DB primary unavailable | Sync audit writer catches SqlException transient codes | Falls back to local file queue; recovery BackgroundService drains on reconnect |
| Upstream timeout | HttpClient timeout | Polly retry policy (transient-only, max 3 attempts with exponential backoff + jitter) |
| Upstream persistent failure | Polly circuit breaker (5 consecutive failures) | Breaker trips; gateway returns 502 to client without hitting upstream |
| Saga step failure | SagaExecutionLog state=failed | SagaOrchestrator walks executed steps in reverse, invokes ICompensatableStep |
| Process crash mid-saga | SagaExecutionLog non-terminal state past 5min | SagaRecoveryService runs sp_CompensateSaga against the correlation id |
| Audit channel full | ChannelAuditMetricsChannel at capacity | DropOldest kicks in; telemetry record lost (best-effort by design) |
| Read replica lag | Reporting API returns slightly stale data | Documented in ADR-0003; acceptable for metrics dashboards |

## Observability

- **Logs**: Serilog to stdout; correlation id enriched on every log line via the AsyncLocal
  CorrelationContext.
- **Metrics**: OpenTelemetry traces via AspNetCore / HttpClient / SqlClient instrumentation.
  CloudWatch alarms on p99, 5xx rate, ASG unhealthy hosts, RDS CPU, and StuckSagaCount
  (custom metric from the scheduled Lambda).
- **Traces**: OpenTelemetry SDK with `ApiGateway` activity source for custom spans.
- **Audit trail**: `AuditLog_Security` (sync, durable, never deleted) and
  `AuditLog_Metrics` (async, 90-day hot retention, then archived to S3 via partition swap).
