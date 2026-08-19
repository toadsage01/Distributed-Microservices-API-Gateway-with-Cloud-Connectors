using ApiGateway.Application.Sagas;
using ApiGateway.Application.Sagas.Steps;
using ApiGateway.Application.Contracts;
using ApiGateway.Domain.Entities;
using ApiGateway.Domain.ValueObjects;
using ApiGateway.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ApiGateway.IntegrationTests.Sagas;

[Collection("SqlServerCollection")]
public sealed class SagaCrashRecoveryTests : IAsyncLifetime
{
    private readonly SqlServerFixture _fixture;
    private ISqlConnectionFactory? _factory;

    public SagaCrashRecoveryTests(SqlServerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _factory = new SqlConnectionFactory(
            Microsoft.Extensions.Options.Options.Create(new ApiGateway.Infrastructure.Configuration.DatabaseOptions
            {
                PrimaryConnectionString = _fixture.ConnectionString
            }),
            NullLogger<SqlConnectionFactory>.Instance);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Recovery_PicksUpFailedSaga_AndCompensatesCompletedSteps()
    {
        // Arrange: simulate a crash mid-saga. Step A completed, step B was
        // running when the process died (state was updated to Failed by
        // operator tooling — or the SagaRecoveryService's first pass).
        var corrId = CorrelationId.New();
        var logRepo = new SagaExecutionLogRepository(_factory!);

        var stepA = SagaExecutionLog.StartNew(corrId, "step_a", 10);
        stepA.MarkRunning();
        stepA.MarkCompleted();
        await logRepo.InsertAsync(stepA);

        var stepB = SagaExecutionLog.StartNew(corrId, "step_b", 20);
        stepB.MarkRunning();
        stepB.MarkFailed();
        await logRepo.InsertAsync(stepB);

        // Act: simulate the recovery path. The orchestrator's RecoverAsync
        // calls sp_CompensateSaga which returns the completed/failed steps
        // in reverse order. For each, the orchestrator invokes the matching
        // ICompensatableStep handler.
        var completedSteps = await logRepo.GetCompensatableStepsAsync(corrId);

        // Act: register a fake step handler keyed by the step name
        var fakeStep = new FakeCompensatableStep("step_a");
        var orchestrator = new SagaOrchestrator(
            logRepo,
            new[] { fakeStep },
            NullLogger<SagaOrchestrator>.Instance);

        var context = new SagaContext
        {
            CorrelationId = corrId,
            Request = new GatewayRequest
            {
                PartnerName = "acme",
                Route = "/x",
                HttpMethod = "POST",
                Body = "{}",
                CorrelationId = corrId
            },
            Partner = new PartnerRouteContext
            {
                PartnerId = PartnerId.New(),
                PartnerName = "acme",
                SchemaVersion = "1.0.0",
                AuthMode = AuthMode.Jwt,
                RateLimitTier = RateLimitTier.Standard
            }
        };

        await orchestrator.RecoverAsync(corrId, context);

        // Assert
        fakeStep.CompensateCalled.Should().BeTrue();
        var postRecovery = await logRepo.GetCompensatableStepsAsync(corrId);
        var stepARecord = postRecovery.Single(s => s.StepName == "step_a");
        stepARecord.State.Should().Be(SagaState.Compensated);
    }

    private sealed class FakeCompensatableStep : ICompensatableStep
    {
        public FakeCompensatableStep(string name) { Name = name; }
        public string Name { get; }
        public int Order => 10;
        public bool CompensateCalled { get; private set; }

        public Task<StepResult> ExecuteAsync(SagaContext context, CancellationToken ct = default)
            => Task.FromResult(StepResult.Ok());

        public Task CompensateAsync(SagaContext context, string? payloadSnapshot, CancellationToken ct = default)
        {
            CompensateCalled = true;
            return Task.CompletedTask;
        }
    }
}
