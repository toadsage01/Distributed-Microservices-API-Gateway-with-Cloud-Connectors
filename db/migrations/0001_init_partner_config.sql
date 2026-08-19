-- Migration: 0001_init_partner_config
-- Purpose: Partner routing/policy table. Loaded once per inbound request
--          by the PartnerConfigRepository to drive schema validation,
--          rate limit tier, and the transformation ruleset reference.
--
-- Notes:
--   * PartnerId is a GUID surrogate so we can re-key partner display names
--     without cascading FK updates through SagaExecutionLog / AuditLog_*.
--   * RateLimitTier is nvarchar(50) rather than an int enum — the schema
--     must remain decodable from raw SQL ops without a code lookup table.
--   * Default IsActive=1 so a fresh INSERT (e.g. via ops tooling) starts
--     routing traffic immediately; explicit deactivation goes via UPDATE.

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PartnerConfig')
BEGIN
    CREATE TABLE PartnerConfig (
        PartnerId                  UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
        PartnerName                NVARCHAR(200)    NOT NULL,
        SchemaVersion              NVARCHAR(20)     NOT NULL,
        TransformationRulesetRef   NVARCHAR(500)    NULL,
        RateLimitTier              NVARCHAR(50)     NOT NULL DEFAULT 'standard',
        AuthMode                   NVARCHAR(50)     NOT NULL,
        IsActive                   BIT              NOT NULL DEFAULT 1,
        CreatedAt                  DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME()
    );

    CREATE UNIQUE INDEX UX_PartnerConfig_PartnerName
        ON PartnerConfig(PartnerName)
        WHERE IsActive = 1;

    CREATE INDEX IX_PartnerConfig_AuthMode_IsActive
        ON PartnerConfig(AuthMode, IsActive);
END
GO
