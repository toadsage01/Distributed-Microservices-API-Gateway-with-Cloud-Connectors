using ApiGateway.Application.Abstractions;
using ApiGateway.Domain.ValueObjects;
using ApiGateway.Infrastructure.Persistence.Repositories;

namespace ApiGateway.Application.Infrastructure;

/// <summary>
/// Adapter that wraps the IIdempotencyKeyRepository with the
/// IIdempotencyKeyResolver interface used by the application layer.
/// Kept separate so the application layer doesn't depend directly on
/// infrastructure types — the dependency arrow stays pointing inward.
/// </summary>
public sealed class IdempotencyKeyResolver : IIdempotencyKeyResolver
{
    private readonly IIdempotencyKeyRepository _repo;

    public IdempotencyKeyResolver(IIdempotencyKeyRepository repo)
    {
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
    }

    public Task<Contracts.CachedResponse?> TryGetAsync(PartnerId partnerId, string idempotencyKey, string requestHash, CancellationToken ct = default)
        => _repo.TryGetAsync(partnerId, idempotencyKey, requestHash, ct)!;

    public Task StoreAsync(PartnerId partnerId, string idempotencyKey, string requestHash, int statusCode, string? body, CorrelationId correlationId, CancellationToken ct = default)
        => _repo.StoreAsync(partnerId, idempotencyKey, requestHash, statusCode, body, correlationId, TimeSpan.FromHours(24), ct);
}
