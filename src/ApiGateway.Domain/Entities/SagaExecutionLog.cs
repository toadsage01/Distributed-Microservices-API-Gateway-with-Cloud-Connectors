using ApiGateway.Domain.Exceptions;
using ApiGateway.Domain.ValueObjects;

namespace ApiGateway.Domain.Entities;

/// <summary>
/// One row per saga step, persisted before the step actually runs. The
/// recovery BackgroundService on process restart looks for sagas whose
/// steps are stuck in Pending/Running for longer than the staleness threshold
/// and re-runs sp_CompensateSaga against their correlation id.
/// </summary>
public class SagaExecutionLog
{
    public Guid Id { get; private set; }
    public CorrelationId CorrelationId { get; private set; }
    public string StepName { get; private set; }
    public int StepOrder { get; private set; }
    public SagaState State { get; private set; }
    public string? PayloadSnapshot { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    private SagaExecutionLog(
        Guid id,
        CorrelationId correlationId,
        string stepName,
        int stepOrder,
        SagaState state,
        string? payloadSnapshot,
        DateTime createdAt,
        DateTime updatedAt)
    {
        if (string.IsNullOrWhiteSpace(stepName))
            throw new SagaStateException("StepName is required");
        if (stepOrder < 0)
            throw new SagaStateException("StepOrder must be >= 0");

        Id = id;
        CorrelationId = correlationId;
        StepName = stepName;
        StepOrder = stepOrder;
        State = state;
        PayloadSnapshot = payloadSnapshot;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    public static SagaExecutionLog StartNew(CorrelationId correlationId, string stepName, int stepOrder, string? payloadSnapshot = null)
    {
        var now = DateTime.UtcNow;
        return new SagaExecutionLog(Guid.NewGuid(), correlationId, stepName, stepOrder, SagaState.Pending, payloadSnapshot, now, now);
    }

    public static SagaExecutionLog Rehydrate(
        Guid id,
        Guid correlationId,
        string stepName,
        int stepOrder,
        SagaState state,
        string? payloadSnapshot,
        DateTime createdAt,
        DateTime updatedAt)
    {
        return new SagaExecutionLog(id, new CorrelationId(correlationId), stepName, stepOrder, state, payloadSnapshot, createdAt, updatedAt);
    }

    public void MarkRunning(string? payloadSnapshot = null)
    {
        TransitionTo(SagaState.Running);
        if (payloadSnapshot is not null) PayloadSnapshot = payloadSnapshot;
    }

    public void MarkCompleted(string? payloadSnapshot = null)
    {
        TransitionTo(SagaState.Completed);
        if (payloadSnapshot is not null) PayloadSnapshot = payloadSnapshot;
    }

    public void MarkCompensating()
    {
        // Compensating can be reached from Completed (normal rollback) or Failed
        // (the recovery runner picking up an orphaned saga after a crash).
        // SagaStateTransitions handles both.
        TransitionTo(SagaState.Compensating);
    }

    public void MarkCompensated() => TransitionTo(SagaState.Compensated);

    public void MarkFailed() => TransitionTo(SagaState.Failed);

    public bool IsTerminal => State is SagaState.Compensated or SagaState.Failed;

    private void TransitionTo(SagaState next)
    {
        SagaStateTransitions.Transition(State, next);
        State = next;
        UpdatedAt = DateTime.UtcNow;
    }
}
