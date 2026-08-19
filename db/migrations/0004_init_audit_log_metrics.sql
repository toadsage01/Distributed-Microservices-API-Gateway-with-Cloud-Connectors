-- Migration: 0004_init_audit_log_metrics
-- Purpose: High-volume request telemetry (latency, payload size, status).
--          Written via SqlBulkCopy from a Channel<T> batch — synchronous
--          inserts would dominate p99 latency at scale.
--
-- Notes:
--   * Partition by OccurredAt (monthly). The right-bound partition function
--     means inserts are always against the latest month's partition, which
--     keeps the buffer pool hot.
--   * Retention is 90 days hot then archived to S3 via scheduled job — the
--     partition swap is the cheapest way to age out data without blocking
--     the hot insert path.
--   * Route is nvarchar(300) to accommodate the /v1/{partner}/{rest of path}
--     shape without truncation.

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'AuditLog_Metrics')
BEGIN
    CREATE PARTITION FUNCTION PF_AuditLog_Metrics_Monthly (DATETIME2)
        AS RANGE RIGHT FOR VALUES ();

    CREATE PARTITION SCHEME PS_AuditLog_Metrics_Monthly
        AS PARTITION PF_AuditLog_Metrics_Monthly ALL TO ([PRIMARY]);

    CREATE TABLE AuditLog_Metrics (
        Id                BIGINT IDENTITY PRIMARY KEY,
        PartnerId         UNIQUEIDENTIFIER NULL,
        Route             NVARCHAR(300) NOT NULL,
        LatencyMs         INT            NOT NULL,
        PayloadSizeBytes  INT            NULL,
        StatusCode       INT            NOT NULL,
        OccurredAt       DATETIME2      NOT NULL DEFAULT SYSUTCDATETIME()
    ) ON PS_AuditLog_Metrics_Monthly(OccurredAt);

    CREATE INDEX IX_AuditLog_Metrics_OccurredAt
        ON AuditLog_Metrics(OccurredAt);

    CREATE INDEX IX_AuditLog_Metrics_PartnerId_OccurredAt
        ON AuditLog_Metrics(PartnerId, OccurredAt)
        INCLUDE (LatencyMs, StatusCode)
        WHERE PartnerId IS NOT NULL;
END
GO

-- Migration: 0004b_seed_metrics_partitions
-- Purpose: Pre-create the current and next month's partitions so the
--          partition switch at month boundary doesn't race an insert.
--
-- This is intentionally split out from the table creation so it can be
-- re-run as part of the monthly partition-rotation maintenance job
-- without re-executing the table DDL.

DECLARE @now DATETIME2 = SYSUTCDATETIME();
DECLARE @firstOfThisMonth DATETIME2 = DATETIMEFROMPARTS(YEAR(@now), MONTH(@now), 1, 0, 0, 0, 0);
DECLARE @firstOfNextMonth DATETIME2 = DATEADD(month, 1, @firstOfThisMonth);
DECLARE @firstOfMonthAfter DATETIME2 = DATEADD(month, 2, @firstOfThisMonth);

-- The partition function is initially empty (RANGE RIGHT with no boundary
-- points means everything lands in partition 1). We add the two boundaries
-- that place rows for this month and next month in their own partitions.

IF NOT EXISTS (SELECT 1 FROM sys.partition_range_values WHERE function_id = (
        SELECT function_id FROM sys.partition_functions WHERE name = 'PF_AuditLog_Metrics_Monthly'
    ))
BEGIN
    ALTER PARTITION SCHEME PS_AuditLog_Metrics_Monthly
        NEXT USED [PRIMARY];

    ALTER PARTITION FUNCTION PF_AuditLog_Metrics_Monthly()
        SPLIT RANGE (@firstOfThisMonth);

    ALTER PARTITION SCHEME PS_AuditLog_Metrics_Monthly
        NEXT USED [PRIMARY];

    ALTER PARTITION FUNCTION PF_AuditLog_Metrics_Monthly()
        SPLIT RANGE (@firstOfNextMonth);
END
GO
