using ApiGateway.Domain.ValueObjects;
using FluentAssertions;
using Xunit;

namespace ApiGateway.UnitTests.Domain;

public class SagaStateTransitionTests
{
    [Theory]
    [InlineData(SagaState.Pending, SagaState.Running, true)]
    [InlineData(SagaState.Running, SagaState.Completed, true)]
    [InlineData(SagaState.Running, SagaState.Failed, true)]
    [InlineData(SagaState.Completed, SagaState.Compensating, true)]
    [InlineData(SagaState.Compensating, SagaState.Compensated, true)]
    [InlineData(SagaState.Compensating, SagaState.Failed, true)]
    [InlineData(SagaState.Failed, SagaState.Compensating, true)]
    [InlineData(SagaState.Pending, SagaState.Completed, false)]
    [InlineData(SagaState.Pending, SagaState.Compensating, false)]
    [InlineData(SagaState.Compensated, SagaState.Compensating, false)]
    [InlineData(SagaState.Compensated, SagaState.Pending, false)]
    public void CanTransition_EncodesLegalGraph(SagaState from, SagaState to, bool expected)
    {
        SagaStateTransitions.CanTransition(from, to).Should().Be(expected);
    }
}
