-- Migration: 0003_init_audit_log_security
-- Purpose: Synchronous-path audit log for security events (auth failures,
--          rate-limit breaches, schema rejections). Volume is low because
--          these only fire on rejection, so a trigger is affordable here.
--
-- Notes:
--   * Composite index on (EventType, OccurredAt) supports the trigger's
--     "more than N events of type X in window W for partner P" scan.
--   * PartnerId is nullable because some events (e.g. malformed JWT before
--     partner resolution) won't have one yet.

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'AuditLog_Security')
BEGIN
    CREATE TABLE AuditLog_Security (
        Id            BIGINT IDENTITY PRIMARY KEY,
        EventType     NVARCHAR(100) NOT NULL,
        PartnerId     UNIQUEIDENTIFIER NULL,
        Detail        NVARCHAR(MAX)   NULL,
        OccurredAt    DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME()
    );

    CREATE INDEX IX_AuditLog_Security_EventType_OccurredAt
        ON AuditLog_Security(EventType, OccurredAt)
        INCLUDE (PartnerId);

    CREATE INDEX IX_AuditLog_Security_PartnerId_OccurredAt
        ON AuditLog_Security(PartnerId, OccurredAt)
        WHERE PartnerId IS NOT NULL;
END
GO
