using ApiGateway.Domain.Entities;
using ApiGateway.Domain.Exceptions;
using ApiGateway.Domain.ValueObjects;
using FluentAssertions;
using Xunit;

namespace ApiGateway.UnitTests.Domain;

public class SagaExecutionLogTests
{
    [Fact]
    public void StartNew_InitialStateIsPending()
    {
        var log = SagaExecutionLog.StartNew(CorrelationId.New(), "step_a", 1);
        log.State.Should().Be(SagaState.Pending);
        log.Id.Should().NotBeEmpty();
        log.StepOrder.Should().Be(1);
    }

    [Fact]
    public void StateTransitions_FollowLegalGraph()
    {
        var log = SagaExecutionLog.StartNew(CorrelationId.New(), "step_a", 1);

        log.MarkRunning();
        log.State.Should().Be(SagaState.Running);

        log.MarkCompleted();
        log.State.Should().Be(SagaState.Completed);

        log.MarkCompensating();
        log.State.Should().Be(SagaState.Compensating);

        log.MarkCompensated();
        log.State.Should().Be(SagaState.Compensated);
        log.IsTerminal.Should().BeTrue();
    }

    [Fact]
    public void FailedState_AllowsCompensatingTransition()
    {
        // The recovery runner can re-enter compensation from Failed
        // state — that's the whole point of crash recovery.
        var log = SagaExecutionLog.StartNew(CorrelationId.New(), "step_a", 1);
        log.MarkRunning();
        log.MarkFailed();

        var act = () => log.MarkCompensating();
        act.Should().NotThrow<SagaStateException>();
        log.State.Should().Be(SagaState.Compensating);
    }

    [Fact]
    public void IllegalTransition_Throws()
    {
        var log = SagaExecutionLog.StartNew(CorrelationId.New(), "step_a", 1);
        // Pending -> Completed is not in the legal graph
        var act = () => log.MarkCompleted();
        act.Should().Throw<SagaStateException>();
    }

    [Fact]
    public void MarkCompleted_WithSnapshot_UpdatesPayloadSnapshot()
    {
        var log = SagaExecutionLog.StartNew(CorrelationId.New(), "step_a", 1);
        log.MarkRunning();
        log.MarkCompleted(payloadSnapshot: """{"url":"https://upstream/cb"}""");

        log.PayloadSnapshot.Should().Contain("upstream/cb");
    }
}
