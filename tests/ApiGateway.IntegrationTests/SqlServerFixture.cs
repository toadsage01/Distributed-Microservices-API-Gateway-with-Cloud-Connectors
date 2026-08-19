using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;
using Xunit;

namespace ApiGateway.IntegrationTests;

/// <summary>
/// Testcontainers fixture that boots a real SQL Server instance per
/// test session. The container is started once and shared across all
/// integration tests in the assembly — test classes use the same
/// connection string but each test rolls back its own changes via a
/// transaction-scoped cleanup.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder()
        .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
        .WithPassword("IntegrationTestPass123!")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await ApplyMigrationsAsync();
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
    }

    private async Task ApplyMigrationsAsync()
    {
        // Apply the migration scripts directly against the container so the
        // integration tests can exercise the repositories without invoking
        // the .NET MigrationRunner (which we test separately).
        var here = AppContext.BaseDirectory;
        var repoRoot = FindRepoRoot(here);
        var dirs = new[] { "migrations", "stored-procedures", "triggers" };

        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();

        foreach (var dir in dirs)
        {
            var dirPath = Path.Combine(repoRoot, "db", dir);
            if (!Directory.Exists(dirPath)) continue;

            foreach (var file in Directory.GetFiles(dirPath, "*.sql").OrderBy(f => f))
            {
                var script = await File.ReadAllTextAsync(file);
                foreach (var batch in SplitOnGo(script))
                {
                    if (string.IsNullOrWhiteSpace(batch)) continue;
                    await using var cmd = new SqlCommand(batch, conn);
                    cmd.CommandTimeout = 60;
                    await cmd.ExecuteNonQueryAsync();
                }
            }
        }
    }

    private static string FindRepoRoot(string startPath)
    {
        var dir = new DirectoryInfo(startPath);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ApiGateway.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate repo root from " + startPath);
    }

    private static IReadOnlyList<string> SplitOnGo(string script)
    {
        var lines = script.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        var batches = new List<string>();
        var current = new System.Text.StringBuilder();

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

[CollectionDefinition("SqlServerCollection")]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture> { }
