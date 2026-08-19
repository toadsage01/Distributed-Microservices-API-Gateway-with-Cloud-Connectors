-- Migration: 0002_init_saga_execution_log
-- Purpose: Persisted saga step state. The orchestrator writes one row per
--          step before invoking it, so a process crash leaves a recovery
--          trail the BackgroundService can replay on restart.
--
-- Notes:
--   * CorrelationId is indexed (not unique) because each saga has many steps.
--   * State is nvarchar(30) so DBAs can read state at a glance — using an
--     int enum here would have made ad-hoc ops queries inscrutable.
--   * PayloadSnapshot is NVARCHAR(MAX) — JSON serialised, used by the
--     compensation handlers to reverse upstream side effects.

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'SagaExecutionLog')
BEGIN
    CREATE TABLE SagaExecutionLog (
        Id                UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
        CorrelationId     UNIQUEIDENTIFIER NOT NULL,
        StepName          NVARCHAR(200)    NOT NULL,
        StepOrder         INT              NOT NULL,
        State             NVARCHAR(30)     NOT NULL, -- pending/running/completed/compensating/compensated/failed
        PayloadSnapshot   NVARCHAR(MAX)   NULL,
        CreatedAt         DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
        UpdatedAt         DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT CK_SagaExecutionLog_State CHECK
            (State IN ('pending','running','completed','compensating','compensated','failed')),
        CONSTRAINT CK_SagaExecutionLog_StepOrder CHECK (StepOrder >= 0)
    );

    CREATE INDEX IX_SagaExecutionLog_CorrelationId
        ON SagaExecutionLog(CorrelationId)
        INCLUDE (StepOrder, State);

    -- Recovery runner looks for non-terminal sagas older than the staleness
    -- threshold; this index makes that scan cheap.
    CREATE INDEX IX_SagaExecutionLog_State_UpdatedAt
        ON SagaExecutionLog(State, UpdatedAt)
        WHERE State IN ('pending','running','compensating');
END
GO
