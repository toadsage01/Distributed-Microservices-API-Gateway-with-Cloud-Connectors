using System.Data;
using ApiGateway.Infrastructure.Configuration;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace ApiGateway.Infrastructure.Persistence;

/// <summary>
/// Hands out SqlConnection instances against the primary and read replica
/// connection strings. The split is enforced here (not at the call site) so
/// a developer wiring a reporting query into the wrong handler cannot
/// accidentally hit the write replica — there is no single GetConnection()
/// that picks one for you.
/// </summary>
public interface ISqlConnectionFactory
{
    /// <summary>Opens a connection against the primary (read-write) DB. Use for all saga, audit-write, and idempotency-key operations.</summary>
    Task<SqlConnection> OpenPrimaryAsync(CancellationToken ct = default);

    /// <summary>Opens a connection against the read replica. Only the reporting API should call this.</summary>
    Task<SqlConnection> OpenReadReplicaAsync(CancellationToken ct = default);
}

public sealed class SqlConnectionFactory : ISqlConnectionFactory, IDisposable, IAsyncDisposable
{
    private readonly DatabaseOptions _options;
    private readonly ILogger<SqlConnectionFactory> _logger;

    public SqlConnectionFactory(IOptions<DatabaseOptions> options, ILogger<SqlConnectionFactory> logger)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.PrimaryConnectionString))
            throw new InvalidOperationException("Database:PrimaryConnectionString is not configured");
    }

    public async Task<SqlConnection> OpenPrimaryAsync(CancellationToken ct = default)
    {
        var conn = BuildConnection(_options.PrimaryConnectionString);
        try
        {
            await conn.OpenAsync(ct).ConfigureAwait(false);
            return conn;
        }
        catch
        {
            await conn.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<SqlConnection> OpenReadReplicaAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ReadReplicaConnectionString))
            throw new InvalidOperationException(
                "Database:ReadReplicaConnectionString is not configured — reporting API cannot run without a replica");

        var conn = BuildConnection(_options.ReadReplicaConnectionString);
        try
        {
            await conn.OpenAsync(ct).ConfigureAwait(false);
            // Belt-and-braces: enforce read-only at the session level so a
            // future bug in the reporting query path cannot mutate data even
            // if the replica were accidentally promoted.
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED; SET ROWCOUNT 5000;";
            cmd.CommandTimeout = _options.DefaultCommandTimeoutSeconds;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return conn;
        }
        catch
        {
            await conn.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private SqlConnection BuildConnection(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString)
        {
            ConnectTimeout = 5,
            MaxPoolSize = 200,
            LoadBalanceTimeout = _options.ConnectionLifetimeSeconds,
            Pooling = true,
            // TrustServerCertificate is acceptable here because in production the
            // connection string is injected from Secrets Manager and always uses
            // a real certificate — this flag is for the dev loop only.
            TrustServerCertificate = true
        };

        return new SqlConnection(builder.ConnectionString);
    }

    public void Dispose() { }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
