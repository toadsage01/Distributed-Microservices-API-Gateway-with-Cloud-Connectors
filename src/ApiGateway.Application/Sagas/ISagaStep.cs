using ApiGateway.Application.Contracts;
using ApiGateway.Domain.ValueObjects;

namespace ApiGateway.Application.Sagas;

/// <summary>
/// A single step in a saga. Steps are forward-only; their compensation
/// counterpart (if any) is exposed via ICompensatableStep. Steps without
/// side effects (pure transforms, aggregations) implement only ISagaStep.
/// </summary>
public interface ISagaStep
{
    /// <summary>Logical name persisted in SagaExecutionLog.StepName. Used to resolve compensation handlers.</summary>
    string Name { get; }

    /// <summary>Sequence within the saga — lower runs first. Two steps with the same Order are non-deterministic.</summary>
    int Order { get; }

    /// <summary>Executes the forward pass. Returns a JSON snapshot used by the compensation handler.</summary>
    Task<StepResult> ExecuteAsync(SagaContext context, CancellationToken ct = default);
}

public interface ICompensatableStep : ISagaStep
{
    /// <summary>
    /// Reverses the side effect committed by ExecuteAsync. The payload
    /// snapshot produced during ExecuteAsync is passed back so the
    /// compensation handler doesn't need to re-fetch any state.
    /// </summary>
    Task CompensateAsync(SagaContext context, string? payloadSnapshot, CancellationToken ct = default);
}

public sealed class StepResult
{
    public bool Success { get; init; }
    public string? PayloadSnapshot { get; init; }
    public string? FailureReason { get; init; }
    public UpstreamResult? UpstreamResult { get; init; }

    public static StepResult Ok(string? snapshot = null, UpstreamResult? upstream = null) => new()
    {
        Success = true,
        PayloadSnapshot = snapshot,
        UpstreamResult = upstream
    };

    public static StepResult Fail(string reason, UpstreamResult? upstream = null) => new()
    {
        Success = false,
        FailureReason = reason,
        UpstreamResult = upstream
    };
}

public sealed class SagaContext
{
    public required CorrelationId CorrelationId { get; init; }
    public required GatewayRequest Request { get; init; }
    public required PartnerRouteContext Partner { get; init; }
    public string? TransformedBody { get; set; }
    public UpstreamResult? LastUpstreamResult { get; set; }
    public string? AggregatedResponse { get; set; }

    /// <summary>Per-step scratchpad for transient state that shouldn't be persisted to PayloadSnapshot.</summary>
    public Dictionary<string, object?> Items { get; } = new();
}
