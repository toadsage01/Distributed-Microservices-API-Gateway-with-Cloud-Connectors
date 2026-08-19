using ApiGateway.Domain.Entities;
using ApiGateway.Infrastructure.Audit;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ApiGateway.Infrastructure.Anomaly;

/// <summary>
/// BackgroundService that subscribes to the IAuditMetricsChannel and
/// feeds every record through the IAnomalyScorer. The same channel is
/// drained by AsyncAuditBatchWriter — both subscribers see every
/// record. The single-writer constraint on the channel refers to the
/// Channel itself, not the subscribers.
/// </summary>
public sealed class AnomalyDetectionService : BackgroundService
{
    private readonly IAuditMetricsChannel _channel;
    private readonly IAnomalyScorer _scorer;
    private readonly ISecurityAlertWriter _writer;
    private readonly AnomalyOptions _options;
    private readonly ILogger<AnomalyDetectionService> _logger;

    public AnomalyDetectionService(
        IAuditMetricsChannel channel,
        IAnomalyScorer scorer,
        ISecurityAlertWriter writer,
        IOptions<AnomalyOptions> options,
        ILogger<AnomalyDetectionService> logger)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _scorer = scorer ?? throw new ArgumentNullException(nameof(scorer));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AnomalyDetectionService started. ZThreshold={Z} MinSamples={Min} AbsCap={Cap}",
            _options.ZScoreThreshold, _options.MinSampleCount, _options.AbsoluteRateCap);

        try
        {
            await foreach (var entry in _channel.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    var alert = _scorer.Score(entry);
                    if (alert is not null)
                    {
                        await _writer.WriteAsync(alert, stoppingToken).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Anomaly scorer threw on entry for partner {PartnerId}", entry.PartnerId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // graceful shutdown
        }
    }
}
