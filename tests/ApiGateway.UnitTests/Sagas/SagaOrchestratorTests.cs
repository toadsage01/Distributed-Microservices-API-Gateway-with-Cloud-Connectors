using ApiGateway.Application.Contracts;
using ApiGateway.Application.Sagas;
using ApiGateway.Application.Sagas.Steps;
using ApiGateway.Domain.Entities;
using ApiGateway.Domain.ValueObjects;
using ApiGateway.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace ApiGateway.UnitTests.Sagas;

public class SagaOrchestratorTests
{
    private readonly Mock<ISagaExecutionLogRepository> _logRepo = new();
    private readonly SagaOrchestrator _orchestrator;

    public SagaOrchestratorTests()
    {
        _orchestrator = new SagaOrchestrator(_logRepo.Object, Array.Empty<ISagaStep>(), NullLogger<SagaOrchestrator>.Instance);
    }

    private static SagaContext NewContext()
    {
        return new SagaContext
        {
            CorrelationId = CorrelationId.New(),
            Request = new GatewayRequest
            {
                PartnerName = "acme",
                Route = "/orders",
                HttpMethod = "POST",
                Body = "{}",
                CorrelationId = CorrelationId.New()
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
    }

    [Fact]
    public async Task ExecuteAsync_NoSteps_ReturnsOkWithEmptyBody()
    {
        var ctx = NewContext();
        var response = await _orchestrator.ExecuteAsync(ctx);
        response.StatusCode.Should().Be(200);
    }

    [Fact]
    public async Task ExecuteAsync_StepThrows_CompensatesPriorSteps()
    {
        // Arrange: step A succeeds (compensatable), step B throws
        var stepA = new FakeStep("step_a", 10, success: true);
        var stepB = new FakeStep("step_b", 20, success: false, throwOnExecute: true);

        var orchestrator = new SagaOrchestrator(
            _logRepo.Object,
            new[] { stepA, stepB },
            NullLogger<SagaOrchestrator>.Instance);

        // Act
        var ctx = NewContext();
        var response = await orchestrator.ExecuteAsync(ctx);

        // Assert
        response.StatusCode.Should().Be(502);
        stepA.ExecuteCalled.Should().BeTrue();
        stepB.ExecuteCalled.Should().BeTrue();
        stepA.CompensateCalled.Should().BeTrue("step A was completed before B threw");
        stepB.CompensateCalled.Should().BeFalse("step B never completed");
    }

    [Fact]
    public async Task ExecuteAsync_StepReturnsFailure_CompensatesPriorSteps()
    {
        var stepA = new FakeStep("step_a", 10, success: true);
        var stepB = new FakeStep("step_b", 20, success: false);

        var orchestrator = new SagaOrchestrator(
            _logRepo.Object,
            new[] { stepA, stepB },
            NullLogger<SagaOrchestrator>.Instance);

        var ctx = NewContext();
        var response = await orchestrator.ExecuteAsync(ctx);

        response.StatusCode.Should().Be(502);
        stepA.CompensateCalled.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_AllStepsSucceed_DoesNotCompensate()
    {
        var stepA = new FakeStep("step_a", 10, success: true);
        var stepB = new FakeStep("step_b", 20, success: true);

        var orchestrator = new SagaOrchestrator(
            _logRepo.Object,
            new[] { stepA, stepB },
            NullLogger<SagaOrchestrator>.Instance);

        var ctx = NewContext();
        var response = await orchestrator.ExecuteAsync(ctx);

        response.StatusCode.Should().Be(200);
        stepA.CompensateCalled.Should().BeFalse();
        stepB.CompensateCalled.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_PersistsEachStepBeforeInvoking()
    {
        var stepA = new FakeStep("step_a", 10, success: true);
        var orchestrator = new SagaOrchestrator(
            _logRepo.Object,
            new[] { stepA },
            NullLogger<SagaOrchestrator>.Instance);

        await orchestrator.ExecuteAsync(NewContext());

        // Insert + 2 Updates (Running, Completed) per step minimum
        _logRepo.Verify(r => r.InsertAsync(It.IsAny<SagaExecutionLog>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        _logRepo.Verify(r => r.UpdateStateAsync(It.IsAny<Guid>(), It.IsAny<SagaState>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.AtLeast(2));
    }

    private sealed class FakeStep : ICompensatableStep
    {
        private readonly bool _success;
        private readonly bool _throwOnExecute;

        public FakeStep(string name, int order, bool success, bool throwOnExecute = false)
        {
            Name = name;
            Order = order;
            _success = success;
            _throwOnExecute = throwOnExecute;
        }

        public string Name { get; }
        public int Order { get; }
        public bool ExecuteCalled { get; private set; }
        public bool CompensateCalled { get; private set; }

        public Task<StepResult> ExecuteAsync(SagaContext context, CancellationToken ct = default)
        {
            ExecuteCalled = true;
            if (_throwOnExecute) throw new InvalidOperationException("boom");
            return Task.FromResult(_success ? StepResult.Ok() : StepResult.Fail("planned failure"));
        }

        public Task CompensateAsync(SagaContext context, string? payloadSnapshot, CancellationToken ct = default)
        {
            CompensateCalled = true;
            return Task.CompletedTask;
        }
    }
}
