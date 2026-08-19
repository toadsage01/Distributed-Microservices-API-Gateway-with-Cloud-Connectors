-- Migration: 0007_add_idempotency_key_table
-- Purpose: Persisted idempotency key store so a retried POST with the same
--          Idempotency-Key header returns the cached upstream response
--          instead of creating a second real-world side effect.
--
-- Notes:
--   * Keyed on (PartnerId, IdempotencyKey) — partners may independently
--     reuse the same opaque key string without colliding.
--   * ResponsePayload is NVARCHAR(MAX) — large upstream responses are
--     cached as-is. TTL via ExpiresAt is enforced by a scheduled cleanup
--     job; the gateway also short-circuits on stale entries.
--   * Status 2xx only — 4xx/5xx are not cached because they're usually
--     retryable failures, not committed side effects.

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'IdempotencyKey')
BEGIN
    CREATE TABLE IdempotencyKey (
        PartnerId        UNIQUEIDENTIFIER NOT NULL,
        IdempotencyKey   NVARCHAR(200)    NOT NULL,
        RequestHash      NVARCHAR(64)     NOT NULL,  -- SHA-256 hex of canonicalised request body
        StatusCode       INT              NOT NULL,
        ResponsePayload  NVARCHAR(MAX)    NULL,
        CorrelationId    UNIQUEIDENTIFIER NOT NULL,
        CreatedAt        DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
        ExpiresAt        DATETIME2        NOT NULL,
        CONSTRAINT PK_IdempotencyKey PRIMARY KEY (PartnerId, IdempotencyKey)
    );

    CREATE INDEX IX_IdempotencyKey_ExpiresAt
        ON IdempotencyKey(ExpiresAt);
END
GO
