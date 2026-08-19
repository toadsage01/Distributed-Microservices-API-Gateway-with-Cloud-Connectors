using ApiGateway.Domain.Entities;
using ApiGateway.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace ApiGateway.Infrastructure.Audit;

/// <summary>
/// Synchronous audit writer for security-relevant events. Tries the primary
/// DB first; on transient failure, falls back to the local file queue so
/// the security event is never lost. The recovery BackgroundService drains
/// the queue on DB reconnect.
///
/// The contract is: when WriteAsync returns, the event is durable either
/// in the DB or in the file queue. Callers (auth middleware, rate-limit
/// middleware) depend on this for correctness.
/// </summary>
public sealed class SyncAuditWriter : ISyncAuditWriter
{
    private readonly ISqlConnectionFactory _factory;
    private readonly IAuditFallbackQueue _fallback;
    private readonly ILogger<SyncAuditWriter> _logger;

    public SyncAuditWriter(
        ISqlConnectionFactory factory,
        IAuditFallbackQueue fallback,
        ILogger<SyncAuditWriter> logger)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task WriteAsync(AuditLogSecurity entry, CancellationToken ct = default)
    {
        try
        {
            await WriteToDbAsync(entry, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            _logger.LogWarning(ex,
                "Primary DB unavailable for audit write — falling back to local queue. EventType={EventType} PartnerId={PartnerId}",
                entry.EventType, entry.PartnerId);

            try
            {
                await _fallback.EnqueueAsync(entry, ct).ConfigureAwait(false);
            }
            catch (Exception fallbackEx)
            {
                // Last-resort: we cannot lose the event. Log loudly and
                // rethrow so the caller can decide whether to fail the
                // request or proceed. In practice the middleware always
                // proceeds — a security audit failure must not break the
                // primary request flow.
                _logger.LogError(fallbackEx,
                    "Fallback queue write failed for audit event {EventType} — event may be lost",
                    entry.EventType);
                throw;
            }
        }
    }

    private async Task WriteToDbAsync(AuditLogSecurity entry, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO AuditLog_Security
                (EventType, PartnerId, Detail, OccurredAt)
            VALUES
                (@eventType, @partnerId, @detail, @occurredAt)
            """;

        await using var conn = await _factory.OpenPrimaryAsync(ct).ConfigureAwait(false);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@eventType", System.Data.SqlDbType.NVarChar, 100).Value = entry.EventType;
        cmd.Parameters.Add("@partnerId", System.Data.SqlDbType.UniqueIdentifier).Value = (object?)entry.PartnerId ?? DBNull.Value;
        cmd.Parameters.Add("@detail", System.Data.SqlDbType.NVarChar).Value = (object?)entry.Detail ?? DBNull.Value;
        cmd.Parameters.Add("@occurredAt", System.Data.SqlDbType.DateTime2).Value = entry.OccurredAt;

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static bool IsTransient(Exception ex)
    {
        // SqlException transient error numbers cover the common cloud DB
        // failure modes: throttling (40501), connection broken (10053/10054),
        // failover (40197), timeout (-2), and the standard service-bus /
        // connection pool exhaustion codes.
        if (ex is SqlException sql)
        {
            foreach (SqlError err in sql.Errors)
            {
                if (err.Number is -2 or 40197 or 40501 or 40613 or 10053 or 10054 or 11001)
                    return true;
            }
        }
        return ex is TimeoutException;
    }
}
