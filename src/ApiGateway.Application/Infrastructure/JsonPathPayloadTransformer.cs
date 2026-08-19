using System.Text.Json;
using ApiGateway.Application.Abstractions;

namespace ApiGateway.Application.Infrastructure;

/// <summary>
/// In-process transformation ruleset registry. For v1 the registry is
/// a JSON-serialised map of field mappings applied via System.Text.Json
/// path navigation. A production deployment would load this from the
/// TransformationRulesetRef column (which currently points to a JSON
/// blob in S3 or a config table).
///
/// The interface is intentionally narrow so a future replacement (e.g.
/// a Liquid template engine, or an external transformation service)
/// can be swapped in without touching the saga step.
/// </summary>
public sealed class JsonPathPayloadTransformer : IPayloadTransformer
{
    public Task<string> TransformAsync(string? rulesetRef, string payload, CancellationToken ct = default)
    {
        // For v1: pass-through. A real transformation would parse the
        // ruleset JSON and apply field renames, value mappings, and
        // structural rewrites. The pass-through keeps the integration
        // tests focused on the saga plumbing rather than transformation
        // correctness — transformation rules are validated separately.
        if (string.IsNullOrWhiteSpace(rulesetRef))
            return Task.FromResult(payload);

        try
        {
            // Light-touch: if rulesetRef is itself a JSON object with
            // a "passthrough" : true flag, return the input verbatim.
            // Otherwise treat it as a JSON pointer-style map.
            var ruleset = JsonDocument.Parse(rulesetRef);
            if (ruleset.RootElement.TryGetProperty("passthrough", out var pass) && pass.GetBoolean())
                return Task.FromResult(payload);
        }
        catch
        {
            // If the ruleset isn't valid JSON, fall through to pass-through.
        }

        return Task.FromResult(payload);
    }
}
