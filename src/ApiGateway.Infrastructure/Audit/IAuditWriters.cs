using ApiGateway.Domain.Entities;

namespace ApiGateway.Infrastructure.Audit;

/// <summary>
/// Writes security-relevant audit events synchronously — the call returns
/// only after the event is durable either in the database or in the local
/// fallback file queue. This path must never silently drop an event.
/// </summary>
public interface ISyncAuditWriter
{
    Task WriteAsync(AuditLogSecurity entry, CancellationToken ct = default);
}

/// <summary>
/// Pushes high-volume request telemetry into an async channel. The
/// caller never blocks on the database — the batch writer drains the
/// channel in the background via SqlBulkCopy.
/// </summary>
public interface IAsyncAuditWriter
{
    void Enqueue(AuditLogMetrics entry);
}

/// <summary>
/// Audit channel used to share the same buffered queue between the metrics
/// writer and the anomaly detector subscriber. The detector hooks the same
/// channel so it sees every telemetry record without the ingestion path
/// having to know about it.
/// </summary>
public interface IAuditMetricsChannel
{
    ValueTask EnqueueAsync(AuditLogMetrics entry, CancellationToken ct = default);
    IAsyncEnumerable<AuditLogMetrics> ReadAllAsync(CancellationToken ct = default);
}
