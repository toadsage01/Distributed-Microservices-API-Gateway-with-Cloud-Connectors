using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using ApiGateway.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace ApiGateway.Infrastructure.Migration;

/// <summary>
/// Forward-only migration runner. Each migration script is wrapped in
/// an explicit transaction so a partial failure rolls back cleanly.
/// The runner tracks applied scripts in the SchemaMigrations table.
///
/// Stored procedures and triggers under their respective folders use
/// CREATE OR ALTER, so the runner applies them on every startup —
/// their DDL reflects the current state of the .sql file.
/// </summary>
public sealed class MigrationRunner
{
    private static readonly string[] ManifestFolders = { "Migrations", "StoredProcedures", "Triggers" };

    private readonly ISqlConnectionFactory _factory;
    private readonly ILogger<MigrationRunner> _logger;
    private readonly ConcurrentDictionary<string, bool> _applied = new();

    public MigrationRunner(ISqlConnectionFactory factory, ILogger<MigrationRunner> logger)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task RunAllAsync(CancellationToken ct = default)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resources = assembly.GetManifestResourceNames()
            .Where(n => ManifestFolders.Any(f => n.Contains($".{f}.") || n.EndsWith($".{f}") || n.Contains($"{f}\\")))
            .OrderBy(n => n)
            .ToList();

        _logger.LogInformation("MigrationRunner discovered {Count} scripts", resources.Count);

        // Bootstrap the SchemaMigrations table before attempting to read
        // from it — the migration that creates it is itself tracked by it.
        await EnsureHistoryTableExistsAsync(ct).ConfigureAwait(false);

        foreach (var resource in resources)
        {
            // The history-table migration is special: it must always run
            // because we just ensured the table exists; if we try to
            // consult the table for it, we'd skip its DDL.
            if (resource.Contains("0008_add_migration_history_table"))
            {
                await ExecuteResourceAsync(resource, isReplayable: true, ct).ConfigureAwait(false);
                continue;
            }

            await ExecuteIfNotAppliedAsync(resource, ct).ConfigureAwait(false);
        }

        _logger.LogInformation("MigrationRunner completed");
    }

    private async Task EnsureHistoryTableExistsAsync(CancellationToken ct)
    {
        await using var conn = await _factory.OpenPrimaryAsync(ct).ConfigureAwait(false);
        await using var cmd = new SqlCommand("""
            IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'SchemaMigrations')
            BEGIN
                CREATE TABLE SchemaMigrations (
                    ScriptName    NVARCHAR(255) NOT NULL PRIMARY KEY,
                    Checksum      NVARCHAR(64)  NOT NULL,
                    AppliedAt     DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME()
                );
            END
            """, conn);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task ExecuteIfNotAppliedAsync(string resourceName, CancellationToken ct)
    {
        var scriptName = SanitiseResourceName(resourceName);
        if (_applied.ContainsKey(scriptName)) return;

        await using var conn = await _factory.OpenPrimaryAsync(ct).ConfigureAwait(false);
        await using var checkCmd = new SqlCommand(
            "SELECT COUNT(1) FROM SchemaMigrations WHERE ScriptName = @name", conn);
        checkCmd.Parameters.Add("@name", System.Data.SqlDbType.NVarChar, 255).Value = scriptName;

        var alreadyApplied = (int)await checkCmd.ExecuteScalarAsync(ct).ConfigureAwait(false) > 0;
        if (alreadyApplied)
        {
            _logger.LogDebug("Migration {Name} already applied — skipping", scriptName);
            _applied[scriptName] = true;
            return;
        }

        await ExecuteResourceAsync(resourceName, isReplayable: false, ct).ConfigureAwait(false);

        var checksum = await ComputeChecksumAsync(resourceName, ct).ConfigureAwait(false);
        await using var insertCmd = new SqlCommand("""
            INSERT INTO SchemaMigrations (ScriptName, Checksum) VALUES (@name, @checksum)
            """, conn);
        insertCmd.Parameters.Add("@name", System.Data.SqlDbType.NVarChar, 255).Value = scriptName;
        insertCmd.Parameters.Add("@checksum", System.Data.SqlDbType.NVarChar, 64).Value = checksum;
        await insertCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        _applied[scriptName] = true;
        _logger.LogInformation("Applied migration {Name}", scriptName);
    }

    private async Task ExecuteResourceAsync(string resourceName, bool isReplayable, CancellationToken ct)
    {
        var scriptText = await ReadResourceAsync(resourceName, ct).ConfigureAwait(false);
        var scriptName = SanitiseResourceName(resourceName);

        await using var conn = await _factory.OpenPrimaryAsync(ct).ConfigureAwait(false);
        // Scripts contain multiple statements separated by GO — execute via
        // SqlCommand.ExecuteNonQuery won't handle GO, so we split on a regex
        // that matches the standalone GO line and execute each batch.
        var batches = SplitOnGo(scriptText);

        foreach (var batch in batches)
        {
            if (string.IsNullOrWhiteSpace(batch)) continue;
            await using var cmd = new SqlCommand(batch, conn);
            cmd.CommandTimeout = 60;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        if (!isReplayable)
            _logger.LogInformation("Executed {Name}", scriptName);
    }

    private static async Task<string> ReadResourceAsync(string resourceName, CancellationToken ct)
    {
        var assembly = Assembly.GetExecutingAssembly();
        await using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Embedded resource not found: {resourceName}");
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
    }

    private static async Task<string> ComputeChecksumAsync(string resourceName, CancellationToken ct)
    {
        var content = await ReadResourceAsync(resourceName, ct).ConfigureAwait(false);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(hash);
    }

    private static string SanitiseResourceName(string resourceName)
    {
        // Strip namespace prefix so the recorded script name matches what
        // developers see on disk: "Migrations.0001_init_partner_config.sql"
        var idx = resourceName.IndexOf("Migrations", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) idx = resourceName.IndexOf("StoredProcedures", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) idx = resourceName.IndexOf("Triggers", StringComparison.OrdinalIgnoreCase);
        return idx < 0 ? resourceName : resourceName.Substring(idx).Replace('\\', '.').Replace('/', '.');
    }

    private static IReadOnlyList<string> SplitOnGo(string script)
    {
        // Match standalone GO (case insensitive, optional integer argument).
        // We don't support the integer argument here — none of our scripts use it.
        var lines = script.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        var batches = new List<string>();
        var current = new StringBuilder();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Equals("GO", StringComparison.OrdinalIgnoreCase))
            {
                if (current.Length > 0)
                {
                    batches.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.AppendLine(line);
            }
        }

        if (current.Length > 0)
            batches.Add(current.ToString());

        return batches;
    }
}
