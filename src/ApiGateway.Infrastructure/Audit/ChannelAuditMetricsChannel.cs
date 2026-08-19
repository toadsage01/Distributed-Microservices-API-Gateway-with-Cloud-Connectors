using System.Threading.Channels;
using ApiGateway.Domain.Entities;
using ApiGateway.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ApiGateway.Infrastructure.Audit;

/// <summary>
/// Bounded Channel&lt;T&gt; implementation of the metrics audit channel.
/// The bound is configured by AuditOptions.ChannelCapacity — on overflow
/// we drop the telemetry record (not the request). Telemetry is
/// best-effort by design; the synchronous security path is the
/// correctness-critical one.
/// </summary>
public sealed class ChannelAuditMetricsChannel : IAuditMetricsChannel, IAsyncDisposable
{
    private readonly Channel<AuditLogMetrics> _channel;
    private readonly ILogger<ChannelAuditMetricsChannel> _logger;

    public ChannelAuditMetricsChannel(IOptions<AuditOptions> options, ILogger<ChannelAuditMetricsChannel> logger)
    {
        var opts = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _channel = Channel.CreateBounded<AuditLogMetrics>(new BoundedChannelOptions(opts.ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
    }

    public ValueTask EnqueueAsync(AuditLogMetrics entry, CancellationToken ct = default)
    {
        // TryWrite rather than WriteAsync so we can react to backpressure
        // synchronously — WriteAsync would block the request thread, which
        // defeats the point of the async path.
        if (_channel.Writer.TryWrite(entry))
            return ValueTask.CompletedTask;

        // Channel is full and we just dropped the oldest entry. Log at
        // debug because this is an expected condition under burst load
        // and shouldn't generate alert-worthy noise.
        _logger.LogDebug("Audit metrics channel at capacity — dropping oldest entry to make room");
        _channel.Writer.TryWrite(entry);
        return ValueTask.CompletedTask;
    }

    public IAsyncEnumerable<AuditLogMetrics> ReadAllAsync(CancellationToken ct = default)
        => _channel.Reader.ReadAllAsync(ct);

    public ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
