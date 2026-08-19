using ApiGateway.Domain.ValueObjects;

namespace ApiGateway.Application.Contracts;

public sealed class GatewayRequest
{
    public string PartnerName { get; init; } = string.Empty;
    public string Route { get; init; } = string.Empty;
    public string? SchemaVersion { get; init; }
    public string? IdempotencyKey { get; init; }
    public string HttpMethod { get; init; } = "POST";
    public string? Body { get; init; }
    public string? ContentType { get; init; } = "application/json";
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
    public CorrelationId CorrelationId { get; init; }
}

public sealed class GatewayResponse
{
    public int StatusCode { get; init; }
    public string? Body { get; init; }
    public string ContentType { get; init; } = "application/json";
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
    public bool IsFromIdempotencyCache { get; init; }
}

public sealed class PartnerRouteContext
{
    public required PartnerId PartnerId { get; init; }
    public required string PartnerName { get; init; }
    public required string SchemaVersion { get; init; }
    public required AuthMode AuthMode { get; init; }
    public required RateLimitTier RateLimitTier { get; init; }
    public string? TransformationRulesetRef { get; init; }
}

public sealed class UpstreamInvocation
{
    public required string UpstreamName { get; init; }
    public required string Url { get; init; }
    public string HttpMethod { get; init; } = "POST";
    public string? Body { get; init; }
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
}

public sealed class UpstreamResult
{
    public bool Success { get; init; }
    public int StatusCode { get; init; }
    public string? Body { get; init; }
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
    public string? FailureReason { get; init; }
}
