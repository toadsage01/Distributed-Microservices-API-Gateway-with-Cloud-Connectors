-- Stored Procedure: sp_CompensateSaga
-- Purpose: Returns the ordered list of completed steps for the given
--          correlation id, in reverse StepOrder, so compensation handlers
--          know which ICompensatableStep implementations to invoke and in
--          what order. This is callable both from the application's
--          SagaOrchestrator and from operator tooling for manual recovery.
--
-- Returns:
--   Id              UNIQUEIDENTIFIER  -- SagaExecutionLog.Id of the step
--   StepName        NVARCHAR(200)    -- used to resolve the handler
--   StepOrder       INT              -- sequence within the saga
--   State           NVARCHAR(30)     -- 'completed' (or 'failed' for crash recovery)
--   PayloadSnapshot NVARCHAR(MAX)    -- JSON for compensation handlers
--
-- Notes:
--   * This procedure does NOT mutate state — it is a read for compensation
--     planning. The actual state transitions (Compensating/Compensated)
--     happen via UPDATE issued by the orchestrator as each compensation
--     handler runs.
--   * Including the 'failed' state in the result set is intentional: the
--     recovery BackgroundService uses this same proc to find which steps
--     are eligible for compensation after a process crash leaves a saga
--     stuck in 'failed' state.

CREATE OR ALTER PROCEDURE dbo.sp_CompensateSaga
    @CorrelationId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        Id,
        StepName,
        StepOrder,
        State,
        PayloadSnapshot
    FROM dbo.SagaExecutionLog
    WHERE CorrelationId = @CorrelationId
      AND State IN ('completed', 'failed')
    ORDER BY StepOrder DESC;
END
GO
