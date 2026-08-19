using System.Threading.Channels;
using ApiGateway.Domain.Entities;
using ApiGateway.Infrastructure.Audit;
using ApiGateway.Infrastructure.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ApiGateway.IntegrationTests.Audit;

/// <summary>
/// Tests the AsyncAuditBatchWriter end-to-end against a real SQL Server
/// container. Verifies that records pushed into the channel land in the
/// AuditLog_Metrics table in batches.
/// </summary>
public sealed class AsyncAuditBatchWriterLoadTests
{
    [Fact(Skip = "Requires SQL Server fixture wired via constructor; run under IntegrationTests collection")]
    public void PlaceholderForFixtureWiring() { }

    // The actual load test lives in AsyncAuditBatchWriterWithContainerTests
    // below — split because xUnit collection fixtures can't be injected
    // into Theory parameters.
}

[Collection("SqlServerCollection")]
public sealed class AsyncAuditBatchWriterWithContainerTests : IAsyncLifetime
{
    private readonly SqlServerFixture _fixture;
    private SqlConnectionFactory? _factory;

    public AsyncAuditBatchWriterWithContainerTests(SqlServerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _factory = new SqlConnectionFactory(
            Options.Create(new DatabaseOptions { PrimaryConnectionString = _fixture.ConnectionString }),
            NullLogger<SqlConnectionFactory>.Instance);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task BatchWriter_BulkInsertsMetricsUnderLoad()
    {
        // Arrange
        var channel = new ChannelAuditMetricsChannel(
            Options.Create(new AuditOptions { ChannelCapacity = 5000, BatchSize = 100, FlushIntervalMs = 50 }),
            NullLogger<ChannelAuditMetricsChannel>.Instance);

        var writer = new AsyncAuditBatchWriter(
            channel,
            _factory!,
            Options.Create(new AuditOptions { BatchSize = 100, FlushIntervalMs = 50 }),
            NullLogger<AsyncAuditBatchWriter>.Instance);

        using var cts = new CancellationTokenSource();
        var writerTask = writer.ExecuteAsync(cts.Token);

        // Act: enqueue 250 records
        for (var i = 0; i < 250; i++)
        {
            await channel.EnqueueAsync(new AuditLogMetrics(
                partnerId: Guid.NewGuid(),
                route: $"/v1/test/{i}",
                latencyMs: 10 + i,
                statusCode: 200,
                payloadSizeBytes: 100 + i), cts.Token);
        }

        // Wait for the batch writer to flush — give it up to 5s
        await Task.Delay(TimeSpan.FromSeconds(2), cts.Token);

        cts.Cancel();
        await writerTask;

        // Assert: rows landed in AuditLog_Metrics
        await using var conn = new Microsoft.Data.SqlClient.SqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new Microsoft.Data.SqlClient.SqlCommand(
            "SELECT COUNT(*) FROM AuditLog_Metrics WHERE Route LIKE '/v1/test/%'", conn);
        var count = (int)await cmd.ExecuteScalarAsync();
        count.Should().BeGreaterThanOrEqualTo(200, "most of the 250 enqueued records should have flushed");
    }
}
