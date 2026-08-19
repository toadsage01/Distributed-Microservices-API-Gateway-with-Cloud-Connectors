-- Migration: 0005_init_security_alert
-- Purpose: Output of the anomaly detector and the AuditLog_Security trigger's
--          fast-path signal. Polled by the reporting API for the security
--          dashboard.
--
-- Notes:
--   * AnomalyScore is DECIMAL(10,4) — z-scores can go negative, and we want
--     at least 2 decimal places of precision for the threshold comparisons.
--   * RaisedAt is separate from any insert timestamp so a backfilled alert
--     (e.g. from a batch re-score) can preserve the original detection time.

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'SecurityAlert')
BEGIN
    CREATE TABLE SecurityAlert (
        Id             BIGINT IDENTITY PRIMARY KEY,
        PartnerId      UNIQUEIDENTIFIER NULL,
        AnomalyScore   DECIMAL(10,4)    NOT NULL,
        PatternType    NVARCHAR(100)    NOT NULL,
        Detail         NVARCHAR(MAX)    NULL,
        RaisedAt       DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME()
    );

    CREATE INDEX IX_SecurityAlert_PartnerId_RaisedAt
        ON SecurityAlert(PartnerId, RaisedAt DESC)
        WHERE PartnerId IS NOT NULL;

    CREATE INDEX IX_SecurityAlert_RaisedAt
        ON SecurityAlert(RaisedAt DESC);
END
GO
