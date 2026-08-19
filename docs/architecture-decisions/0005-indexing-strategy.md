# ADR 0005: Per-table indexing strategy

Date: 2024-12
Status: Accepted

## Context

The schema has six tables with distinct access patterns. Picking indexes by gut feel
across them leads to either missing indexes (slow queries) or redundant indexes (wasted
write budget on the hot path).

This ADR documents the rationale per table so future migrations don't undo a deliberate
choice without considering the trade-off.

## PartnerConfig

- **PK** on `PartnerId` (default cluster from PRIMARY KEY).
- **UX_PartnerConfig_PartnerName** (unique, filtered `WHERE IsActive = 1`) — the gateway
  resolves a partner by name on every request. Unique constraint prevents accidentally
  creating two active partners with the same name (e.g., from a manual INSERT during
  onboarding). Filtered to inactive so historical partners can be retained without
  blocking the name from being reused.
- **IX_PartnerConfig_AuthMode_IsActive** — supports the partner admin UI's "show me all
  JWT partners" filter.

## SagaExecutionLog

- **PK** on `Id`.
- **IX_SagaExecutionLog_CorrelationId** with `INCLUDE (StepOrder, State)` — every saga
  lookup is `WHERE CorrelationId = @corr` followed by reading the step list. The INCLUDE
  makes it a covering index for the sp_CompensateSaga query.
- **IX_SagaExecutionLog_State_UpdatedAt** filtered `WHERE State IN ('pending','running',
  'compensating')` — the SagaRecoveryService scans for stale sagas every minute. Without
  this filtered index that scan would be a full table over time as terminal-state rows
  accumulate. The filter keeps the index small.

## AuditLog_Security

- **PK** on `Id`.
- **IX_AuditLog_Security_EventType_OccurredAt** with `INCLUDE (PartnerId)` — supports the
  trigger's "more than N events of type X in window W for partner P" scan. The INCLUDE
  avoids a key lookup back to the base table when the trigger fires.
- **IX_AuditLog_Security_PartnerId_OccurredAt** filtered `WHERE PartnerId IS NOT NULL` —
  supports the reporting API's per-partner audit query. Filtered because the null-PartnerId
  rows (auth failures before partner resolution) aren't queried by-partner.

## AuditLog_Metrics

- **PK** on `Id`.
- **Partition scheme** `PF_AuditLog_Metrics_Monthly` (RANGE RIGHT on `OccurredAt`) —
  supports the partition swap for archival without locking the hot insert path. The
  monthly boundary keeps each partition at a manageable size for the bulk delete via
  `ALTER TABLE ... SWITCH PARTITION ... TO ...` pattern.
- **IX_AuditLog_Metrics_OccurredAt** — supports the archival query that picks the oldest
  partition for swap-out.
- **IX_AuditLog_Metrics_PartnerId_OccurredAt** with `INCLUDE (LatencyMs, StatusCode)`
  filtered `WHERE PartnerId IS NOT NULL` — supports the reporting API's per-partner
  percentile query. INCLUDE turns it into a covering index for the `PERCENTILE_CONT(0.99)
  WITHIN GROUP (ORDER BY LatencyMs)` aggregate.

## SecurityAlert

- **PK** on `Id`.
- **IX_SecurityAlert_PartnerId_RaisedAt** descending on `RaisedAt`, filtered `WHERE
  PartnerId IS NOT NULL` — the reporting API's `GET /reporting/alerts?partnerId=X` query
  returns the most recent alerts first. DESC + TOP N makes this an index seek, not a scan.
- **IX_SecurityAlert_RaisedAt** descending — supports the global `GET /reporting/alerts`
  query for the security dashboard's "all recent alerts" view.

## SchemaVersion

- **PK** on `(PartnerId, Version)` composite — natural key. Two partners may independently
  use the same version string.
- **IX_SchemaVersion_PartnerId_EffectiveDate** descending on `EffectiveDate` with
  `INCLUDE (Version)` — the "current version for partner P" lookup is the hottest path in
  the schema validation middleware. The INCLUDE makes it a covering index so the lookup
  doesn't bounce back to the base table.

## IdempotencyKey

- **PK** on `(PartnerId, IdempotencyKey)` composite — natural key, supports the
  TryGetAsync lookup directly.
- **IX_IdempotencyKey_ExpiresAt** — supports the scheduled cleanup job that drops expired
  entries.

## General principles applied

1. **Filter non-null columns.** Wherever a query path always includes `WHERE Col IS NOT
   NULL`, the index is filtered. Keeps the index small and the write cost low.
2. **INCLUDE for covering indexes.** When a query reads columns that aren't part of the
   seek predicate, INCLUDE them rather than adding them to the key. Keeps the index narrower
   and the seek cheaper.
3. **Avoid indexes on the hot write path that don't earn their keep.** The async audit
   writer inserts via `SqlBulkCopy` against `AuditLog_Metrics`; every index on that table
   is paid for on every insert. We accept the partition + two indexes as the minimum set.
4. **No triggers on `AuditLog_Metrics`.** The async write path is the bottleneck — adding
   a trigger here would be paid for on every record. The burst-detection trigger lives on
   `AuditLog_Security` where the write rate is low.

## Re-evaluation

When the access patterns change (new reporting query, new burst-detection signal), revisit
the relevant table's section here before adding an index. The trade-off is documented so
the conversation is informed.
