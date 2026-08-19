using System.Text.Json;
using ApiGateway.Domain.Entities;
using ApiGateway.Infrastructure.Configuration;
using ApiGateway.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ApiGateway.Infrastructure.Audit;

/// <summary>
/// Append-only on-disk queue used when the primary DB is unavailable for
/// sync audit writes. Recovery BackgroundService drains this on reconnect.
///
/// Format: one JSON-serialised event per line, prefixed with a UTC ticks
/// timestamp for ordering during drain. We don't use a binary format
/// because ops may need to inspect the queue contents by hand if the
/// recovery runner itself is misbehaving.
/// </summary>
public interface IAuditFallbackQueue
{
    Task EnqueueAsync(AuditLogSecurity entry, CancellationToken ct = default);
    Task<IReadOnlyList<AuditLogSecurity>> DrainAsync(int maxBatch, CancellationToken ct = default);
    int ApproximateDepth { get; }
}

public sealed class AuditFallbackFileQueue : IAuditFallbackQueue
{
    private readonly string _queuePath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ILogger<AuditFallbackFileQueue> _logger;

    public AuditFallbackFileQueue(IOptions<AuditOptions> options, ILogger<AuditFallbackFileQueue> logger)
    {
        var opts = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _queuePath = opts.FallbackQueuePath;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        Directory.CreateDirectory(_queuePath);
    }

    public int ApproximateDepth
    {
        get
        {
            try { return Directory.GetFiles(_queuePath, "*.audit").Length; }
            catch { return 0; }
        }
    }

    public async Task EnqueueAsync(AuditLogSecurity entry, CancellationToken ct = default)
    {
        var line = string.Format("{0}\t{1}\n",
            entry.OccurredAt.Ticks,
            JsonSerializer.Serialize(entry));

        // Filename includes ticks + a short random suffix so concurrent
        // enqueues from different threads can't collide. The lock below
        // is the actual ordering guard — the suffix is defensive.
        var fileName = Path.Combine(_queuePath,
            string.Format("{0:D19}-{1}.audit", entry.OccurredAt.Ticks, Guid.NewGuid().ToString("N").Substring(0, 8)));

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await File.WriteAllTextAsync(fileName, line, ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<AuditLogSecurity>> DrainAsync(int maxBatch, CancellationToken ct = default)
    {
        var drained = new List<AuditLogSecurity>();

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var files = Directory.GetFiles(_queuePath, "*.audit")
                .OrderBy(f => f)
                .Take(maxBatch)
                .ToArray();

            foreach (var file in files)
            {
                try
                {
                    var content = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
                    var newlineIdx = content.IndexOf('\n');
                    if (newlineIdx < 0) continue;
                    var json = content.AsSpan().Slice(0, newlineIdx).ToString();
                    var tickSep = json.IndexOf('\t');
                    if (tickSep > 0) json = json.Substring(tickSep + 1);

                    var entry = JsonSerializer.Deserialize<AuditLogSecurity>(json);
                    if (entry is not null) drained.Add(entry);
                    File.Delete(file);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to drain audit fallback file {File}; leaving on disk for next pass",
                        file);
                }
            }
        }
        finally
        {
            _lock.Release();
        }

        return drained;
    }
}
