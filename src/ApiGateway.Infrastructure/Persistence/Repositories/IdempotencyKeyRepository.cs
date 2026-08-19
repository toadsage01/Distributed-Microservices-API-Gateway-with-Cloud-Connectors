using ApiGateway.Domain.Entities;
using ApiGateway.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace ApiGateway.Infrastructure.Persistence.Repositories;

public interface IIdempotencyKeyRepository
{
    /// <summary>Tries to fetch the cached response for an idempotency key. Returns null if not found or expired.</summary>
    Task<CachedResponse?> TryGetAsync(Guid partnerId, string idempotencyKey, string requestHash, CancellationToken ct = default);

    /// <summary>Persists a 2xx response so a replayed request returns the cached body. Caller is responsible for ensuring the upstream call has actually succeeded.</summary>
    Task StoreAsync(Guid partnerId, string idempotencyKey, string requestHash, int statusCode, string? responsePayload, Guid correlationId, TimeSpan ttl, CancellationToken ct = default);
}

public sealed class CachedResponse
{
    public int StatusCode { get; init; }
    public string? ResponsePayload { get; init; }
}

public sealed class IdempotencyKeyRepository : IIdempotencyKeyRepository
{
    private readonly ISqlConnectionFactory _factory;

    public IdempotencyKeyRepository(ISqlConnectionFactory factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public async Task<CachedResponse?> TryGetAsync(Guid partnerId, string idempotencyKey, string requestHash, CancellationToken ct = default)
    {
        // We return the cached row only if both the partner/key pair matches AND
        // the request hash matches. A hash mismatch means the partner sent a
        // different request body with the same key — that's an idempotency
        // violation, which we handle by returning null and letting the upstream
        // path produce a 409 elsewhere. The hash collision here is the more
        // important signal than the existence of the key.
        const string sql = """
            SELECT StatusCode, ResponsePayload
            FROM IdempotencyKey WITH (UPDLOCK, HOLDLOCK)
            WHERE PartnerId = @pid
              AND IdempotencyKey = @key
              AND RequestHash = @hash
              AND ExpiresAt > SYSUTCDATETIME()
            """;

        await using var conn = await _factory.OpenPrimaryAsync(ct).ConfigureAwait(false);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@pid", System.Data.SqlDbType.UniqueIdentifier).Value = partnerId;
        cmd.Parameters.Add("@key", System.Data.SqlDbType.NVarChar, 200).Value = idempotencyKey;
        cmd.Parameters.Add("@hash", System.Data.SqlDbType.NVarChar, 64).Value = requestHash;

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null;

        return new CachedResponse
        {
            StatusCode = reader.GetInt32(reader.GetOrdinal("StatusCode")),
            ResponsePayload = reader.IsDBNull(reader.GetOrdinal("ResponsePayload")) ? null : reader.GetString(reader.GetOrdinal("ResponsePayload"))
        };
    }

    public async Task StoreAsync(Guid partnerId, string idempotencyKey, string requestHash, int statusCode, string? responsePayload, Guid correlationId, TimeSpan ttl, CancellationToken ct = default)
    {
        const string sql = """
            MERGE IdempotencyKey WITH (HOLDLOCK) AS target
            USING (SELECT @pid AS PartnerId, @key AS IdempotencyKey) AS source
            ON target.PartnerId = source.PartnerId AND target.IdempotencyKey = source.IdempotencyKey
            WHEN MATCHED AND target.RequestHash = @hash THEN UPDATE SET
                StatusCode = @status,
                ResponsePayload = @payload,
                CorrelationId = @corr,
                CreatedAt = SYSUTCDATETIME(),
                ExpiresAt = DATEADD(second, @ttl, SYSUTCDATETIME())
            WHEN NOT MATCHED THEN INSERT
                (PartnerId, IdempotencyKey, RequestHash, StatusCode, ResponsePayload, CorrelationId, ExpiresAt)
                VALUES
                (@pid, @key, @hash, @status, @payload, @corr, DATEADD(second, @ttl, SYSUTCDATETIME()));
            """;

        await using var conn = await _factory.OpenPrimaryAsync(ct).ConfigureAwait(false);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@pid", System.Data.SqlDbType.UniqueIdentifier).Value = partnerId;
        cmd.Parameters.Add("@key", System.Data.SqlDbType.NVarChar, 200).Value = idempotencyKey;
        cmd.Parameters.Add("@hash", System.Data.SqlDbType.NVarChar, 64).Value = requestHash;
        cmd.Parameters.Add("@status", System.Data.SqlDbType.Int).Value = statusCode;
        cmd.Parameters.Add("@payload", System.Data.SqlDbType.NVarChar).Value = (object?)responsePayload ?? DBNull.Value;
        cmd.Parameters.Add("@corr", System.Data.SqlDbType.UniqueIdentifier).Value = correlationId;
        cmd.Parameters.Add("@ttl", System.Data.SqlDbType.Int).Value = (int)ttl.TotalSeconds;

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
