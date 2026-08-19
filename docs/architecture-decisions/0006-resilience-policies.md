# ADR 0006: Polly resilience policies on outbound HTTP

Date: 2024-12
Status: Accepted

## Context

The gateway makes outbound HTTP calls to upstream services via `HttpUpstreamInvoker`. These
calls can fail transiently (network blip, 503, timeout) or persistently (4xx business logic
failure, 5xx upstream is down). Naive retry on a 4xx would amplify business-logic failures;
no retry on a 503 would surface transient upstream blips to clients.

## Decision

Apply **Microsoft.Extensions.Http.Resilience** (which wraps Polly v8) to the
`IUpstreamInvoker` typed HttpClient. The pipeline, in execution order:

1. **Rate limiter** — concurrent request cap per upstream (prevents one slow upstream from
   exhausting the connection pool).
2. **Timeout** — total request timeout of 30s. Defence in depth alongside the per-call
   CancellationToken from the saga pipeline.
3. **Retry** — exponential backoff (1s, 2s, 4s) with jitter, max 3 attempts. Retries only
   on transient HTTP status codes (408, 429, 500, 502, 503, 504) and `HttpRequestException`
   / `TaskCanceledException`. Never retries on 4xx business-logic errors.
4. **Circuit breaker** — trips after 5 consecutive failures in a 30s sampling window,
   half-open probe after 15s. Breaks the call fast while the upstream is down rather than
   queuing requests behind timeouts.

## Rationale

- **Microsoft.Extensions.Http.Resilience over raw Polly.** The Microsoft wrapper gives us
  config-driven policy composition via `IOptions<HttpRetryPolicyOptions>` — operators can
  tune backoff without a code change. The underlying Polly v8 engine is the same.
- **Typed HttpClient.** The `IUpstreamInvoker` is registered via `AddHttpClient<IUpstreamInvoker,
  HttpUpstreamInvoker>`, which gives us the policy pipeline + connection pool management +
  logging for free. The invoker itself doesn't know about Polly — it just calls
  `client.SendAsync`.
- **Transient-only retry.** Retrying a 401 or a 422 (validation failure) would amplify the
  failure: the partner gets one rejection, and the upstream sees three requests for the same
  business intent. The retry predicate is explicit about which status codes are retryable.
- **Circuit breaker is essential, not optional.** Without it, a slow upstream causes
  cascading timeouts: the request pool fills with in-flight calls waiting to time out,
  which starves new requests. The breaker cuts the call fast after 5 consecutive failures,
  so the gateway can serve other traffic while the upstream recovers.

## Trade-offs

- **Half-open probe introduces a small latency hit.** When the breaker is half-open, the
  next request goes through and is timed. If it fails, the breaker re-opens. This means
  the first request after a recovery period may take longer than expected. Acceptable —
  the alternative is to wait for a fixed delay before probing.
- **Retry amplifies upstream load.** A retry storm on a partial outage can push a struggling
  upstream over the edge. The circuit breaker is the backstop — if retries keep failing,
  the breaker trips and the gateway stops hitting the upstream.
- **Configuration surface.** The policy is configurable, which means operators can misconfigure
  it. The defaults are conservative (3 attempts, 30s timeout, 5-failure breaker).

## Alternatives considered

- **No resilience policies — let the saga orchestrator handle retry.** Rejected because
  saga-level retry doesn't help with non-saga HTTP calls (e.g., schema-version fetches,
  partner-config lookups). HttpClient-level policies cover all outbound calls uniformly.
- **Custom retry logic per call site.** Rejected because consistency matters more than
  per-call tunability — operators need one place to look, not twelve.
- **Polly v7 direct registration.** Rejected because v8 is the current major and the
  Microsoft wrapper is the recommended integration path for .NET 8.

## Future work

- Per-upstream policy overrides. Currently all upstreams share the same retry/breaker
  config. Some upstreams may need different settings (e.g., a payment provider with a
  strict retry budget vs. a read-only inventory service that's safe to retry more
  aggressively). Tracked as v2.
