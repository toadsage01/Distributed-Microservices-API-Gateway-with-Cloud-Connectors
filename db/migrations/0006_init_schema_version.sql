-- Migration: 0006_init_schema_version
-- Purpose: Per-partner JSON schema registry. Composite key (PartnerId, Version)
--          means a partner can have multiple concurrent versions during a
--          rollout — the gateway picks the version whose EffectiveDate is the
--          latest <= now.
--
-- Notes:
--   * JsonSchemaBlob is NVARCHAR(MAX) — JSON schemas are unbounded in size.
--   * EffectiveDate is the application time the version became authoritative,
--     not the insert time. This lets ops publish a version with a future
--     effective date for staged rollouts.

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'SchemaVersion')
BEGIN
    CREATE TABLE SchemaVersion (
        PartnerId        UNIQUEIDENTIFIER NOT NULL,
        Version          NVARCHAR(20)     NOT NULL,
        JsonSchemaBlob   NVARCHAR(MAX)    NOT NULL,
        EffectiveDate    DATETIME2        NOT NULL,
        CONSTRAINT PK_SchemaVersion PRIMARY KEY (PartnerId, Version),
        CONSTRAINT FK_SchemaVersion_PartnerConfig
            FOREIGN KEY (PartnerId) REFERENCES PartnerConfig(PartnerId)
    );

    -- The "current version for partner P" lookup is the hottest path in
    -- the schema validation middleware. This index covers the lookup
    -- without a key lookup back to the base table.
    CREATE INDEX IX_SchemaVersion_PartnerId_EffectiveDate
        ON SchemaVersion(PartnerId, EffectiveDate DESC)
        INCLUDE (Version);
END
GO
