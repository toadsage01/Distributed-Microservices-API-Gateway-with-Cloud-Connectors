namespace ApiGateway.Domain.ValueObjects;

/// <summary>
/// Lifecycle of a single saga step. Transitions are guarded by the orchestrator
/// so callers cannot put a step back into Pending once it has been Completed.
///
/// The two compensation states (Compensating / Compensated) are first-class
/// because the recovery runner needs to distinguish "we tried to undo it" from
/// "we successfully undid it" — that distinction matters for operator triage
/// when a saga is stuck after a crash.
/// </summary>
public enum SagaState
{
    Pending = 0,
    Running = 1,
    Completed = 2,
    Compensating = 3,
    Compensated = 4,
    Failed = 5
}

public static class SagaStateTransitions
{
    private static readonly Dictionary<SagaState, SagaState[]> _allowed = new()
    {
        [SagaState.Pending] = new[] { SagaState.Running },
        [SagaState.Running] = new[] { SagaState.Completed, SagaState.Failed },
        [SagaState.Completed] = new[] { SagaState.Compensating },
        [SagaState.Compensating] = new[] { SagaState.Compensated, SagaState.Failed },
        [SagaState.Compensated] = Array.Empty<SagaState>(),
        [SagaState.Failed] = new[] { SagaState.Compensating },
    };

    public static bool CanTransition(SagaState from, SagaState to)
        => _allowed.TryGetValue(from, out var targets) && Array.IndexOf(targets, to) >= 0;

    public static SagaState Transition(SagaState from, SagaState to)
    {
        if (!CanTransition(from, to))
            throw new Exceptions.SagaStateException(
                $"Illegal saga state transition {from} -> {to}");
        return to;
    }
}
