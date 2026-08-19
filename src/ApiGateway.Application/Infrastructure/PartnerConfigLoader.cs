using ApiGateway.Application.Abstractions;
using ApiGateway.Application.Contracts;
using ApiGateway.Domain.ValueObjects;
using ApiGateway.Infrastructure.Persistence.Repositories;
using Microsoft.Extensions.Logging;

namespace ApiGateway.Application.Infrastructure;

/// <summary>
/// Loads PartnerConfig from the persistence layer and projects it into
/// the PartnerRouteContext the saga pipeline consumes. Caching is at
/// the repository layer (where invalidation on partner config mutation
/// is hooked) — this loader is a thin adapter.
/// </summary>
public sealed class PartnerConfigLoader : IPartnerConfigLoader
{
    private readonly IPartnerConfigRepository _repo;
    private readonly ILogger<PartnerConfigLoader> _logger;

    public PartnerConfigLoader(IPartnerConfigRepository repo, ILogger<PartnerConfigLoader> logger)
    {
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<PartnerRouteContext?> LoadAsync(string partnerName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(partnerName))
            return null;

        var partner = await _repo.GetByNameAsync(partnerName, ct).ConfigureAwait(false);
        if (partner is null || !partner.IsActive)
        {
            _logger.LogDebug("PartnerConfigLoader: partner {Name} not found or inactive", partnerName);
            return null;
        }

        return new PartnerRouteContext
        {
            PartnerId = partner.PartnerId,
            PartnerName = partner.PartnerName,
            SchemaVersion = partner.SchemaVersion,
            AuthMode = partner.AuthMode,
            RateLimitTier = partner.RateLimitTier,
            TransformationRulesetRef = partner.TransformationRulesetRef
        };
    }
}
