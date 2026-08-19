using ApiGateway.Domain.Entities;

namespace ApiGateway.Infrastructure.Anomaly;

/// <summary>
/// Scores an incoming telemetry record against the per-partner baseline.
/// Implementations must be thread-safe — they're called from the
/// BackgroundService that subscribes to the metrics channel.
///
/// The interface is deliberately narrow so a future LLM-backed scorer
/// (e.g. via Semantic Kernel invoking a hosted model for payload semantic
/// drift) can be swapped in without touching the ingestion pipeline.
/// </summary>
public interface IAnomalyScorer
{
    /// <summary>Returns null if no anomaly is detected; otherwise the alert to write.</summary>
    SecurityAlert? Score(AuditLogMetrics entry);
}

public interface ISecurityAlertWriter
{
    Task WriteAsync(SecurityAlert alert, CancellationToken ct = default);
}

public interface IPartnerBaselineStore
{
    PartnerBaseline GetOrAdd(Guid partnerId);
    Task PersistCheckpointAsync(CancellationToken ct = default);
}
