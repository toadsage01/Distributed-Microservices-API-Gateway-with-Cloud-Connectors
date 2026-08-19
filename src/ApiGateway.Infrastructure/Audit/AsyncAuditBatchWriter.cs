using System.Data;
using ApiGateway.Domain.Entities;
using ApiGateway.Infrastructure.Configuration;
using ApiGateway.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ApiGateway.Infrastructure.Audit;

/// <summary>
/// BackgroundService that drains the IAuditMetricsChannel and bulk-inserts
/// records into AuditLog_Metrics via SqlBulkCopy. Flushes when either
/// BatchSize records accumulate or FlushIntervalMs elapses since the last
/// flush — whichever comes first.
/// </summary>
public sealed class AsyncAuditBatchWriter : BackgroundService
{
    private readonly IAuditMetricsChannel _channel;
    private readonly ISqlConnectionFactory _factory;
    private readonly AuditOptions _options;
    private readonly ILogger<AsyncAuditBatchWriter> _logger;

    public AsyncAuditBatchWriter(
        IAuditMetricsChannel channel,
        ISqlConnectionFactory factory,
        IOptions<AuditOptions> options,
        ILogger<AsyncAuditBatchWriter> logger)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AsyncAuditBatchWriter started. BatchSize={BatchSize} FlushIntervalMs={FlushIntervalMs}",
            _options.BatchSize, _options.FlushIntervalMs);

        var batch = new List<AuditLogMetrics>(_options.BatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                batch.Clear();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                cts.CancelAfter(_options.FlushIntervalMs);

                await foreach (var entry in _channel.ReadAllAsync(cts.Token).ConfigureAwait(false))
                {
                    batch.Add(entry);
                    if (batch.Count >= _options.BatchSize)
                        break;
                }

                if (batch.Count > 0)
                {
                    await FlushAsync(batch, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AsyncAuditBatchWriter flush failed — entries will be retried next cycle");
                await Task.Delay(_options.FlushIntervalMs, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task FlushAsync(IReadOnlyList<AuditLogMetrics> batch, CancellationToken ct)
    {
        var table = BuildDataTable(batch);

        await using var conn = await _factory.OpenPrimaryAsync(ct).ConfigureAwait(false);
        using var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.TableLock, null)
        {
            DestinationTableName = "AuditLog_Metrics",
            BatchSize = batch.Count,
            BulkCopyTimeout = 30
        };

        bulk.ColumnMappings.Add("PartnerId", "PartnerId");
        bulk.ColumnMappings.Add("Route", "Route");
        bulk.ColumnMappings.Add("LatencyMs", "LatencyMs");
        bulk.ColumnMappings.Add("PayloadSizeBytes", "PayloadSizeBytes");
        bulk.ColumnMappings.Add("StatusCode", "StatusCode");
        bulk.ColumnMappings.Add("OccurredAt", "OccurredAt");

        await bulk.WriteToServerAsync(table, ct).ConfigureAwait(false);
        _logger.LogDebug("Bulk-inserted {Count} metrics records", batch.Count);
    }

    private static DataTable BuildDataTable(IReadOnlyList<AuditLogMetrics> batch)
    {
        var table = new DataTable();
        table.Columns.Add("PartnerId", typeof(Guid));
        table.Columns.Add("Route", typeof(string));
        table.Columns.Add("LatencyMs", typeof(int));
        table.Columns.Add("PayloadSizeBytes", typeof(int));
        table.Columns.Add("StatusCode", typeof(int));
        table.Columns.Add("OccurredAt", typeof(DateTime));

        foreach (var m in batch)
        {
            table.Rows.Add(
                m.PartnerId.HasValue ? m.PartnerId.Value : (object)DBNull.Value,
                m.Route,
                m.LatencyMs,
                m.PayloadSizeBytes.HasValue ? m.PayloadSizeBytes.Value : (object)DBNull.Value,
                m.StatusCode,
                m.OccurredAt);
        }

        return table;
    }
}
