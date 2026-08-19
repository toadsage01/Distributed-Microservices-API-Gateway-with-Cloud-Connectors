-- Trigger: trg_AuditLog_Security_AuthFailureBurst
-- Purpose: When an AUTH_FAILURE event is written, check whether the partner
--          has accumulated more than @Threshold failures within the rolling
--          window. If so, raise a SecurityAlert with pattern_type
--          'auth_failure_burst' so the security team is notified via the
--          same alert surface the anomaly detector uses — independently of
--          the async metrics pipeline.
--
-- Notes:
--   * This is a AFTER INSERT trigger — we don't want the insert to fail
--     if the alert write fails, so the alert insert is wrapped in a
--     TRY/CATCH. The alert is a best-effort signal, not a hard constraint.
--   * Threshold and window are deliberately hardcoded in this trigger
--     rather than read from a config table: triggers fire on every insert,
--     and a config-table lookup per insert adds lock pressure we don't
--     need. Tuning changes go through a migration that ALTERs the trigger.
--   * Threshold of 10 failures in 60s is conservative — the async anomaly
--     detector's z-score path catches bursts at finer granularity, this
--     trigger is the backstop for the case where the async pipeline is
--     lagging.

CREATE OR ALTER TRIGGER dbo.trg_AuditLog_Security_AuthFailureBurst
ON dbo.AuditLog_Security
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @Threshold INT = 10;
    DECLARE @WindowSeconds INT = 60;

    -- Only the inserted AUTH_FAILURE rows are interesting. Other event
    -- types pass through without the burst check.
    DECLARE @affected TABLE (PartnerId UNIQUEIDENTIFIER NULL, OccurredAt DATETIME2 NOT NULL);
    INSERT INTO @affected (PartnerId, OccurredAt)
    SELECT i.PartnerId, i.OccurredAt
    FROM inserted i
    WHERE i.EventType = 'AUTH_FAILURE'
      AND i.PartnerId IS NOT NULL;

    IF NOT EXISTS (SELECT 1 FROM @affected)
        RETURN;

    -- For each affected partner, count failures in the trailing window
    -- ending at the latest inserted event time. If we cross the threshold,
    -- emit one alert (per partner, per trigger firing) — duplicates will
    -- be de-duplicated downstream by the alert dashboard's grouping.
    DECLARE @partner UNIQUEIDENTIFIER;
    DECLARE @cutoff DATETIME2;

    DECLARE partner_cursor CURSOR LOCAL FAST_FORWARD FOR
        SELECT PartnerId, MAX(OccurredAt) FROM @affected GROUP BY PartnerId;

    OPEN partner_cursor;
    FETCH NEXT FROM partner_cursor INTO @partner, @cutoff;

    WHILE @@FETCH_STATUS = 0
    BEGIN
        DECLARE @count INT = (
            SELECT COUNT(*)
            FROM dbo.AuditLog_Security
            WHERE PartnerId = @partner
              AND EventType = 'AUTH_FAILURE'
              AND OccurredAt >= DATEADD(second, -@WindowSeconds, @cutoff)
              AND OccurredAt <= @cutoff
        );

        IF @count >= @Threshold
        BEGIN
            BEGIN TRY
                INSERT INTO dbo.SecurityAlert
                    (PartnerId, AnomalyScore, PatternType, Detail, RaisedAt)
                VALUES
                    (@partner, -1.0, 'auth_failure_burst',
                     CONCAT('Trigger fast-path: ', @count, ' AUTH_FAILURE events in ', @WindowSeconds, 's'),
                     @cutoff);
            END TRY
            BEGIN CATCH
                -- Alert write failure must not block the audit log insert.
                -- The error is logged but the transaction continues.
                DECLARE @msg NVARCHAR(2048) = ERROR_MESSAGE();
                PRINT CONCAT('trg_AuditLog_Security_AuthFailureBurst: alert insert failed: ', @msg);
            END CATCH
        END

        FETCH NEXT FROM partner_cursor INTO @partner, @cutoff;
    END

    CLOSE partner_cursor;
    DEALLOCATE partner_cursor;
END
GO
