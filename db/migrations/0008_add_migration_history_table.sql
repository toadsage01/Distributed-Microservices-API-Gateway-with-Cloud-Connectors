-- Migration: 0008_add_migration_history_table
-- Purpose: SchemaMigrations tracks which migration scripts have been applied
--          so the .NET MigrationRunner can skip already-applied scripts on
--          a forward-only basis.
--
-- Notes:
--   * We use the script filename (e.g. "0001_init_partner_config.sql") as
--     the natural key rather than a numeric version column — this keeps
--     the migration files on disk self-describing and avoids an off-by-one
--     risk if anyone ever renumbers them.
--   * Checksum (SHA-256 of the file contents) is recorded but not enforced
--     — drift detection is a follow-up. We don't want a migration that
--     legitimately fixed a typo to be un-runnable without manual surgery.

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'SchemaMigrations')
BEGIN
    CREATE TABLE SchemaMigrations (
        ScriptName    NVARCHAR(255) NOT NULL PRIMARY KEY,
        Checksum      NVARCHAR(64)  NOT NULL,
        AppliedAt     DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME()
    );
END
GO
