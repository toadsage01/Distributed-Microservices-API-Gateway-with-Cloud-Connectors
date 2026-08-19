using System.Collections.Concurrent;
using ApiGateway.Infrastructure.Persistence.Repositories;

namespace ApiGateway.Infrastructure.Anomaly;

public sealed class InMemoryPartnerBaselineStore : IPartnerBaselineStore
{
    private readonly ConcurrentDictionary<Guid, PartnerBaseline> _baselines = new();
    private readonly ISecurityAlertRepository _alertRepo;
    private readonly ISecurityAlertWriter _alertWriter;

    public InMemoryPartnerBaselineStore(
        ISecurityAlertRepository alertRepo,
        ISecurityAlertWriter alertWriter)
    {
        _alertRepo = alertRepo ?? throw new ArgumentNullException(nameof(alertRepo));
        _alertWriter = alertWriter ?? throw new ArgumentNullException(nameof(alertWriter));
    }

    public PartnerBaseline GetOrAdd(Guid partnerId)
        => _baselines.GetOrAdd(partnerId, id => new PartnerBaseline(id));

    public Task PersistCheckpointAsync(CancellationToken ct = default)
    {
        // Baselines are intentionally not persisted in v1 — on cold start the
        // scorer refuses to score until MinSampleCount samples accumulate,
        // which is fast enough that the gap is acceptable. Persisting them
        // would require a new table and we're deferring that complexity.
        return Task.CompletedTask;
    }
}
