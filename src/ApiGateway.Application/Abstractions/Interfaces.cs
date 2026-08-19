using ApiGateway.Application.Contracts;
using ApiGateway.Domain.ValueObjects;

namespace ApiGateway.Application.Abstractions;

public interface IPartnerConfigLoader
{
    Task<PartnerRouteContext?> LoadAsync(string partnerName, CancellationToken ct = default);
}

public interface ISchemaValidator
{
    Task<bool> ValidateAsync(string jsonSchema, string payload, out string? validationError, CancellationToken ct = default);
}

public interface IPayloadTransformer
{
    /// <summary>Resolves and applies the named transformation ruleset to the inbound payload.</summary>
    Task<string> TransformAsync(string? rulesetRef, string payload, CancellationToken ct = default);
}

public interface IUpstreamInvoker
{
    Task<UpstreamResult> InvokeAsync(UpstreamInvocation invocation, CancellationToken ct = default);
}

public interface IIdempotencyKeyResolver
{
    /// <summary>Returns the cached response if (partner, key, hash) all match and the entry hasn't expired.</summary>
    Task<Contracts.CachedResponse?> TryGetAsync(PartnerId partnerId, string idempotencyKey, string requestHash, CancellationToken ct = default);

    Task StoreAsync(PartnerId partnerId, string idempotencyKey, string requestHash, int statusCode, string? body, CorrelationId correlationId, CancellationToken ct = default);
}

public interface ICorrelationContextAccessor
{
    CorrelationId Current { get; set; }
}
