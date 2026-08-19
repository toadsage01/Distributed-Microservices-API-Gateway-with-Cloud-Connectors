# ADR 0002: Hybrid sync/async audit logging split

Date: 2024-12
Status: Accepted

## Context

The gateway must write two kinds of audit records:

1. **Security-relevant events** — auth failures, rate-limit breaches, schema-validation
   rejections. Volume is low (these only fire on rejection), but they must be durable before
   the response returns. The trigger-based burst detector depends on these rows existing
   synchronously.

2. **High-volume telemetry** — latency, payload size, routing decisions, success-path
   metrics. Volume scales with request rate; a synchronous write on the hot path would
   dominate p99 latency and risk lock contention under real throughput.

A single audit strategy (either all-sync or all-async) is wrong for one of these.

## Decision

Split the audit path:

- **Sync (ADO.NET, pre-response)**: writes security events to `AuditLog_Security`. On DB
  unavailability, falls back to a local append-only file queue drained by a recovery
  `BackgroundService` on reconnect. The contract is: when `WriteAsync` returns, the event
  is durable either in the DB or in the file queue.

- **Async (Channel<T> → BackgroundService → SqlBulkCopy)**: writes telemetry to
  `AuditLog_Metrics`. Batched every 250ms or 500 records, whichever comes first. On
  channel overflow we drop the oldest entry (telemetry is best-effort; security events
  are correctness-critical).

## Rationale

- **Latency.** Synchronous inserts on every request would add ~5-15ms per request even with
  a warm connection pool — at our expected throughput that's a 10-30% latency increase.
  Bulk copy amortises the round-trip across many records.
- **Lock contention.** Trigger-heavy synchronous writes on `AuditLog_Metrics` would compete
  with the saga state machine writes on `SagaExecutionLog` for the same lock pool. The async
  batch writer's `SqlBulkCopy` with `TableLock` is a single brief lock per batch, not per row.
- **Resilience.** Security events cannot be silently dropped — the trigger on
  `AuditLog_Security` would never fire, and the burst detector would miss credential
  stuffing attacks. The local file queue guarantees eventual delivery.
- **Operational simplicity.** A single path for each kind of event means the failure mode
  is predictable. If sync audit writes start failing, we know exactly which subset of the
  pipeline is affected (security events on the fallback path; telemetry on the drop-oldest
  path).

## Trade-offs

- **Two write paths.** More code, more tests. Mitigated by the shared
  `IAuditMetricsChannel` interface — the anomaly detector subscribes to the same channel
  as the async writer, so we don't fan out twice.
- **Telemetry can be lost.** Under burst load, the oldest telemetry record may be dropped
  to make room. This is an explicit trade — alerting on telemetry under burst is the wrong
  layer; the trigger-based fast path on `AuditLog_Security` covers burst detection
  independently of the async pipeline.
- **File queue on disk.** Requires the audit fallback path to exist and be writable.
  Configured via `AuditOptions.FallbackQueuePath` — defaults to
  `/var/lib/apigateway/audit-fallback` in production.

## Alternatives considered

- **All-async, no sync path.** Rejected because the trigger on `AuditLog_Security` requires
  the row to exist before the response returns — otherwise the burst detector doesn't see
  the failure until the next batch flush, which can be 250ms late.
- **All-sync, no async path.** Rejected because the write amplification on `AuditLog_Metrics`
  would dominate p99 latency at scale.
- **External log shipper (Fluent Bit / Vector) writing to S3.** Considered for v2 —
  moves the batching concern out of the app, but adds an operational dependency and
  introduces a delay between event and observability that doesn't fit the burst-detection
  use case.
