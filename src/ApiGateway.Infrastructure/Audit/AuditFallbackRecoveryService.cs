using ApiGateway.Infrastructure.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ApiGateway.Infrastructure.Audit;

/// <summary>
/// BackgroundService that drains the local file fallback queue when the
/// primary DB is reachable again. Each cycle pulls up to BatchSize entries,
/// hands them to the sync writer, and removes the corresponding file on
/// confirmed persistence. The writer does not retry indefinitely within
/// one cycle — if the DB is still down, the queue keeps its files for
/// the next cycle.
/// </summary>
public sealed class AuditFallbackRecoveryService : BackgroundService
{
    private readonly IAuditFallbackQueue _queue;
    private readonly ISyncAuditWriter _writer;
    private readonly AuditOptions _options;
    private readonly ILogger<AuditFallbackRecoveryService> _logger;

    public AuditFallbackRecoveryService(
        IAuditFallbackQueue queue,
        ISyncAuditWriter writer,
        IOptions<AuditOptions> options,
        ILogger<AuditFallbackRecoveryService> logger)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AuditFallbackRecoveryService started. Interval={Interval}s",
            _options.FallbackRecoveryIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_queue.ApproximateDepth > 0)
                {
                    _logger.LogInformation("Draining {Depth} fallback audit entries", _queue.ApproximateDepth);
                    var drained = await _queue.DrainAsync(_options.BatchSize, stoppingToken).ConfigureAwait(false);
                    foreach (var entry in drained)
                    {
                        await _writer.WriteAsync(entry, stoppingToken).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AuditFallbackRecoveryService cycle failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_options.FallbackRecoveryIntervalSeconds), stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
