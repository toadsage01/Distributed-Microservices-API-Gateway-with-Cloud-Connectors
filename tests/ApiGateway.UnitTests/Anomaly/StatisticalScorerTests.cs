using ApiGateway.Domain.Entities;
using ApiGateway.Infrastructure.Anomaly;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ApiGateway.UnitTests.Anomaly;

public class StatisticalScorerTests
{
    private static StatisticalScorer NewScorer(InMemoryPartnerBaselineStore store, AnomalyOptions? options = null)
    {
        var opts = Options.Create(options ?? new AnomalyOptions
        {
            MinSampleCount = 5,
            ZScoreThreshold = 2.5,
            AbsoluteRateCap = 100
        });
        return new StatisticalScorer(store, opts, NullLogger<StatisticalScorer>.Instance);
    }

    [Fact]
    public void Score_BelowMinSamples_ReturnsNull()
    {
        var store = new InMemoryPartnerBaselineStore(
            new Mock<ApiGateway.Infrastructure.Persistence.Repositories.ISecurityAlertRepository>().Object,
            new Mock<ISecurityAlertWriter>().Object);
        var scorer = NewScorer(store, new AnomalyOptions { MinSampleCount = 10 });

        var entry = NewMetric(partnerId: Guid.NewGuid(), payloadSize: 1000);
        var alert = scorer.Score(entry);
        alert.Should().BeNull();
    }

    [Fact]
    public void Score_NoPartnerId_ReturnsNull()
    {
        var store = new InMemoryPartnerBaselineStore(
            new Mock<ApiGateway.Infrastructure.Persistence.Repositories.ISecurityAlertRepository>().Object,
            new Mock<ISecurityAlertWriter>().Object);
        var scorer = NewScorer(store);

        var entry = new AuditLogMetrics(null, "/v1/x", 10, 200, 100);
        var alert = scorer.Score(entry);
        alert.Should().BeNull();
    }

    [Fact]
    public void Score_PayloadSizeDrift_AfterMinSamples_FiresAlert()
    {
        var partnerId = Guid.NewGuid();
        var store = new InMemoryPartnerBaselineStore(
            new Mock<ApiGateway.Infrastructure.Persistence.Repositories.ISecurityAlertRepository>().Object,
            new Mock<ISecurityAlertWriter>().Object);
        var scorer = NewScorer(store, new AnomalyOptions
        {
            MinSampleCount = 5,
            ZScoreThreshold = 2.0,
            AbsoluteRateCap = 1000
        });

        // Warm up the baseline with consistent payload sizes
        for (var i = 0; i < 10; i++)
        {
            scorer.Score(NewMetric(partnerId, payloadSize: 100));
        }

        // Now an outlier
        var alert = scorer.Score(NewMetric(partnerId, payloadSize: 10_000));
        alert.Should().NotBeNull();
        alert!.PatternType.Should().Be(AnomalyPatternTypes.PayloadShapeDrift);
        alert.AnomalyScore.Should().BeGreaterThan(0);
    }

    private static AuditLogMetrics NewMetric(Guid partnerId, int payloadSize)
        => new(partnerId, "/v1/test", 50, 200, payloadSize);
}
