# ADR 0003: CQRS-lite scope boundary

Date: 2024-12
Status: Accepted

## Context

The gateway needs both a write path (the saga pipeline that mutates upstream state and
audit logs) and a read path (the reporting API that serves metrics dashboards and
security-alert views).

Full CQRS with an event store is heavyweight — you get a write model that emits events, a
projection that consumes them and builds a materialised read model, and the operational
complexity of running both. Lite CQRS is just "separate the read and write paths
physically (different connection strings, different hosts) without an event store".

## Decision

Adopt **CQRS-lite** for the gateway:

- Write path (the gateway host) uses the primary RDS connection string.
- Read path (the reporting host) uses the read replica connection string.
- Both paths share the same database — no separate materialised views, no event store.
- The `SqlConnectionFactory` enforces the split at the connection-factory level. There is no
  `GetConnection()` method that picks one — there's `OpenPrimaryAsync` and
  `OpenReadReplicaAsync`.

## Rationale

- **Read/write isolation.** Heavy reporting queries against `AuditLog_Metrics` (p99
  percentile computation over a 24-hour window) shouldn't compete with the write path's
  saga state machine inserts. A read replica gives us physical isolation.
- **No event store.** We don't need event replay for the gateway use case — the
  `SagaExecutionLog` already serves as the durable history. Adding an event store would
  be re-inventing that table with extra ceremony.
- **Operational simplicity.** A read replica is one RDS configuration; an event store is
  another service with its own backup/restore story, retention policy, and replay tooling.
- **Connection-factory enforcement.** Future developers can't accidentally wire a reporting
  query into the gateway host — the connection factory doesn't expose a single
  `GetConnection()` method. The split is in the type system, not just convention.

## Scope boundary

This is **not** full CQRS:

- No materialised views. Reporting queries hit the same physical tables as the write path
  (just on the replica).
- No async projection. Reads see writes at the replication lag of the read replica (typically
  <100ms) — eventually consistent, but not via an event bus.
- No separate read model code. Query handlers live in the application layer alongside
  command handlers; they just use a different connection factory.

## Trade-offs

- **Replication lag.** The reporting API sees writes with a delay. Acceptable for the
  metrics dashboard use case; would not be acceptable for a transactional read after a
  write (which the gateway doesn't have).
- **Schema migrations affect both paths.** A migration that adds a column to
  `AuditLog_Metrics` propagates to the replica automatically, but if we wanted to break
  the schema compatibility between primary and replica we'd need to revisit.

## Alternatives considered

- **Full CQRS with an event store** (e.g., EventStoreDB, DynamoDB Streams + projections).
  Rejected for v1 — operational complexity outweighs the benefit at our scale.
- **Single connection string.** Rejected because heavy reporting queries would directly
  impact write-path latency.
- **Separate database for reporting** (ETL'd from primary). Rejected for v1 — adds an ETL
  pipeline and a synchronisation lag that's worse than read replica lag.
