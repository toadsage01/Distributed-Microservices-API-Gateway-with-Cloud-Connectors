using ApiGateway.Infrastructure.Migration;
using ApiGateway.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ApiGateway.IntegrationTests.Infrastructure;

/// <summary>
/// Smoke test that the MigrationRunner is idempotent — running it twice
/// against the same DB should be a no-op the second time.
/// </summary>
[Collection("SqlServerCollection")]
public sealed class MigrationRunnerTests : IAsyncLifetime
{
    private readonly SqlServerFixture _fixture;
    private SqlConnectionFactory? _factory;

    public MigrationRunnerTests(SqlServerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _factory = new SqlConnectionFactory(
            Options.Create(new ApiGateway.Infrastructure.Configuration.DatabaseOptions
            {
                PrimaryConnectionString = _fixture.ConnectionString
            }),
            NullLogger<SqlConnectionFactory>.Instance);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task RunAllTwice_SecondRunIsNoOp()
    {
        // The fixture already applied migrations on init. Calling the runner
        // here should be a no-op because everything is in SchemaMigrations.
        var runner = new MigrationRunner(_factory!, NullLogger<MigrationRunner>.Instance);
        await runner.RunAllAsync();
        await runner.RunAllAsync(); // idempotent — no exception

        // Verify the SchemaMigrations history has entries
        await using var conn = new Microsoft.Data.SqlClient.SqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new Microsoft.Data.SqlClient.SqlCommand(
            "SELECT COUNT(*) FROM SchemaMigrations", conn);
        var count = (int)await cmd.ExecuteScalarAsync();
        count.Should().BeGreaterThan(0);
    }
}
