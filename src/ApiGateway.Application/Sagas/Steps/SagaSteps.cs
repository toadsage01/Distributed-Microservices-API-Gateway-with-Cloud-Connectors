using System.Text.Json;
using ApiGateway.Application.Abstractions;
using ApiGateway.Application.Contracts;
using ApiGateway.Domain.ValueObjects;

namespace ApiGateway.Application.Sagas.Steps;

/// <summary>
/// Resolves and applies the named transformation ruleset to the inbound
/// payload. The result is stored on SagaContext.TransformedBody so
/// subsequent CallUpstreamStep uses the canonical internal model.
///
/// Not compensatable — pure transformation has no side effects to undo.
/// </summary>
public sealed class TransformPayloadStep : ISagaStep
{
    private readonly IPayloadTransformer _transformer;
    public string Name => "transform_payload";
    public int Order => 10;

    public TransformPayloadStep(IPayloadTransformer transformer)
    {
        _transformer = transformer ?? throw new ArgumentNullException(nameof(transformer));
    }

    public async Task<StepResult> ExecuteAsync(SagaContext context, CancellationToken ct = default)
    {
        try
        {
            var transformed = await _transformer.TransformAsync(
                context.Partner.TransformationRulesetRef,
                context.Request.Body ?? string.Empty,
                ct).ConfigureAwait(false);

            context.TransformedBody = transformed;
            return StepResult.Ok(snapshot: JsonSerializer.Serialize(new { inputBytes = (context.Request.Body?.Length ?? 0), outputBytes = transformed.Length }));
        }
        catch (Exception ex)
        {
            return StepResult.Fail($"transform failed: {ex.Message}");
        }
    }
}

/// <summary>
/// Calls a single upstream endpoint via the IUpstreamInvoker. Persisted
/// payload snapshot includes the upstream URL and response status — this
/// is what the compensation handler uses to decide whether the upstream
/// effect needs to be reversed.
///
/// Compensatable. The default compensation strategy is to invoke an
/// upstream-defined "rollback" URL — if the upstream contract doesn't
/// expose one, this step is registered without a rollback URL and the
/// compensation handler logs and returns successfully (the operator
/// dashboard surfaces "manual intervention" alerts).
/// </summary>
public sealed class CallUpstreamStep : ICompensatableStep
{
    private readonly IUpstreamInvoker _invoker;
    private readonly UpstreamInvocation _invocation;
    private readonly string? _compensationUrl;

    public string Name => $"call_upstream_{_invocation.UpstreamName}";
    public int Order { get; }

    public CallUpstreamStep(IUpstreamInvoker invoker, UpstreamInvocation invocation, int order, string? compensationUrl = null)
    {
        _invoker = invoker ?? throw new ArgumentNullException(nameof(invoker));
        _invocation = invocation ?? throw new ArgumentNullException(nameof(invocation));
        Order = order;
        _compensationUrl = compensationUrl;
    }

    public async Task<StepResult> ExecuteAsync(SagaContext context, CancellationToken ct = default)
    {
        var body = context.TransformedBody ?? context.Request.Body ?? string.Empty;
        var result = await _invoker.InvokeAsync(_invocation, ct).ConfigureAwait(false);

        if (!result.Success)
        {
            return StepResult.Fail(
                reason: $"upstream {_invocation.UpstreamName} returned {result.StatusCode}: {result.FailureReason}",
                upstream: result);
        }

        var snapshot = JsonSerializer.Serialize(new
        {
            upstream = _invocation.UpstreamName,
            url = _invocation.Url,
            requestHash = SHA256Hex(body),
            statusCode = result.StatusCode,
            compensationUrl = _compensationUrl
        });

        context.LastUpstreamResult = result;
        return StepResult.Ok(snapshot, result);
    }

    public async Task CompensateAsync(SagaContext context, string? payloadSnapshot, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_compensationUrl))
        {
            // The upstream contract didn't declare a rollback endpoint —
            // compensation is a no-op. The saga log will record this step
            // as Compensated but operators must verify via the upstream
            // system that no residual effect remains.
            return;
        }

        // Reuse the invoker to POST a compensation callback. The body
        // is the original payload snapshot so the upstream knows what
        // to reverse.
        var compensation = new UpstreamInvocation
        {
            UpstreamName = $"{_invocation.UpstreamName}_compensation",
            Url = _compensationUrl,
            HttpMethod = "POST",
            Body = payloadSnapshot,
            Headers = _invocation.Headers
        };

        var result = await _invoker.InvokeAsync(compensation, ct).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new InvalidOperationException(
                $"Compensation call to {_compensationUrl} failed with status {result.StatusCode}: {result.FailureReason}");
        }
    }

    private static string SHA256Hex(string input)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes);
    }
}

/// <summary>
/// Aggregates results from prior steps into the final response payload.
/// Pure — no side effects — so not compensatable.
/// </summary>
public sealed class AggregateResponseStep : ISagaStep
{
    public string Name => "aggregate_response";
    public int Order => 1000;

    public Task<StepResult> ExecuteAsync(SagaContext context, CancellationToken ct = default)
    {
        // Simple aggregation for v1: pass through the last upstream body
        // wrapped in a standard envelope. Future versions may merge
        // multiple upstream responses.
        var upstreamBody = context.LastUpstreamResult?.Body ?? "{}";
        context.AggregatedResponse = JsonSerializer.Serialize(new
        {
            data = JsonSerializer.Deserialize<JsonElement>(upstreamBody),
            correlationId = context.CorrelationId.Value.ToString("D")
        });

        return Task.FromResult(StepResult.Ok());
    }
}
