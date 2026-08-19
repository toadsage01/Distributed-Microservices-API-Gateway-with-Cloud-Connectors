using ApiGateway.Domain.Entities;
using ApiGateway.Infrastructure.Audit;
using Microsoft.Extensions.Logging;

namespace ApiGateway.Infrastructure.Audit;

/// <summary>
/// Adapter that implements IAsyncAuditWriter (the simple Enqueue surface used
/// by the request pipeline) on top of the shared IAuditMetricsChannel. The
/// channel is shared so the anomaly detector subscriber sees the same
/// telemetry stream without the request path having to fan out twice.
/// </summary>
public sealed class ChannelAsyncAuditWriter : IAsyncAuditWriter
{
    private readonly IAuditMetricsChannel _channel;
    private readonly ILogger<ChannelAsyncAuditWriter> _logger;

    public ChannelAsyncAuditWriter(IAuditMetricsChannel channel, ILogger<ChannelAsyncAuditWriter> logger)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void Enqueue(AuditLogMetrics entry)
    {
        // Fire-and-forget ValueTask — caller is on the hot path and does
        // not care about completion. If the channel rejects (which only
        // happens during shutdown), we log and move on.
        var vt = _channel.EnqueueAsync(entry);
        if (vt.IsCompletedSuccessfully)
            return;

        _ = ObserveAsync(vt);
    }

    private async Task ObserveAsync(ValueTask vt)
    {
        try { await vt.ConfigureAwait(false); }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Audit metrics enqueue failed — entry dropped");
        }
    }
}
