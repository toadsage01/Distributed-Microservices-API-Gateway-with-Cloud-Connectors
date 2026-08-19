using ApiGateway.Domain.Entities;
using ApiGateway.Domain.ValueObjects;
using ApiGateway.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ApiGateway.IntegrationTests.Sagas;

[Collection("SqlServerCollection")]
public sealed class SagaRecoveryIntegrationTests : IAsyncLifetime
{
    private readonly SqlServerFixture _fixture;
    private ISqlConnectionFactory? _factory;

    public SagaRecoveryIntegrationTests(SqlServerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _factory = new SqlConnectionFactory(
            Microsoft.Extensions.Options.Options.Create(new ApiGateway.Infrastructure.Configuration.DatabaseOptions
            {
                PrimaryConnectionString = _fixture.ConnectionString,
                ReadReplicaConnectionString = string.Empty,
                ConnectionLifetimeSeconds = 60,
                DefaultCommandTimeoutSeconds = 30
            }),
            NullLogger<SqlConnectionFactory>.Instance);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task SpCompensateSaga_ReturnsCompletedStepsInReverseOrder()
    {
        // Arrange: insert three saga log rows for the same correlation id
        var corrId = CorrelationId.New();
        var repo = new SagaExecutionLogRepository(_factory!);

        var step1 = SagaExecutionLog.StartNew(corrId, "step_a", 10);
        var step2 = SagaExecutionLog.StartNew(corrId, "step_b", 20);
        var step3 = SagaExecutionLog.StartNew(corrId, "step_c", 30);

        step1.MarkRunning();
        step1.MarkCompleted();
        step2.MarkRunning();
        step2.MarkCompleted();
        step3.MarkRunning();
        step3.MarkFailed();

        await repo.InsertAsync(step1);
        await repo.InsertAsync(step2);
        await repo.InsertAsync(step3);

        // Act
        var steps = await repo.GetCompensatableStepsAsync(corrId);

        // Assert: completed and failed steps come back in reverse StepOrder
        steps.Should().HaveCount(3);
        steps[0].StepName.Should().Be("step_c");
        steps[0].State.Should().Be(SagaState.Failed);
        steps[1].StepName.Should().Be("step_b");
        steps[1].State.Should().Be(SagaState.Completed);
        steps[2].StepName.Should().Be("step_a");
        steps[2].State.Should().Be(SagaState.Completed);
    }

    [Fact]
    public async Task UpdateStateAsync_TransitionsPersist()
    {
        var corrId = CorrelationId.New();
        var repo = new SagaExecutionLogRepository(_factory!);

        var step = SagaExecutionLog.StartNew(corrId, "step_x", 1);
        await repo.InsertAsync(step);

        await repo.UpdateStateAsync(step.Id, SagaState.Running, null);
        await repo.UpdateStateAsync(step.Id, SagaState.Completed, """{"snapshot":true}""");

        var compensatable = await repo.GetCompensatableStepsAsync(corrId);
        compensatable.Should().ContainSingle();
        compensatable[0].State.Should().Be(SagaState.Completed);
        compensatable[0].PayloadSnapshot.Should().Contain("snapshot");
    }

    [Fact]
    public async Task GetStaleSagaStepIds_ReturnsOnlyNonTerminalPastThreshold()
    {
        var corrId = CorrelationId.New();
        var repo = new SagaExecutionLogRepository(_factory!);

        var step = SagaExecutionLog.StartNew(corrId, "step_stale", 1);
        step.MarkRunning(); // running is non-terminal

        // Manually backdate UpdatedAt by inserting then UPDATE-ing
        await repo.InsertAsync(step);
        await using var conn = new SqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand("""
            UPDATE SagaExecutionLog
            SET UpdatedAt = DATEADD(minute, -10, SYSUTCDATETIME())
            WHERE Id = @id
            """, conn);
        cmd.Parameters.AddWithValue("@id", step.Id);
        await cmd.ExecuteNonQueryAsync();

        // Act
        var stale = await repo.GetStaleSagaStepIdsAsync(TimeSpan.FromMinutes(5));

        // Assert
        stale.Should().Contain(step.Id);
    }
}
