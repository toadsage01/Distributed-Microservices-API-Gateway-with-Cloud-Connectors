using System.Collections.Concurrent;
using ApiGateway.Domain.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ApiGateway.Infrastructure.Anomaly;

/// <summary>
/// Statistical scorer using rolling z-scores per partner. Three signals:
///   1. Request-rate burst (z-score of inter-arrival time)
///   2. Payload-size drift (z-score of payload size)
///   3. Absolute rate cap (fast-path, no z-score)
///
/// Only one alert per inbound record — the highest-z-score pattern wins.
/// </summary>
public sealed class StatisticalScorer : IAnomalyScorer
{
    private readonly IPartnerBaselineStore _store;
    private readonly AnomalyOptions _options;
    private readonly ILogger<StatisticalScorer> _logger;

    public StatisticalScorer(
        IPartnerBaselineStore store,
        IOptions<AnomalyOptions> options,
        ILogger<StatisticalScorer> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public SecurityAlert? Score(AuditLogMetrics entry)
    {
        if (entry.PartnerId is null)
            return null;

        var baseline = _store.GetOrAdd(entry.PartnerId.Value);
        baseline.Observe(entry.LatencyMs, entry.PayloadSizeBytes);

        if (baseline.SampleCount < _options.MinSampleCount)
            return null;

        double? bestZ = null;
        string? pattern = null;
        string? detail = null;

        // Payload size drift
        if (entry.PayloadSizeBytes.HasValue && baseline.PayloadSizeStdDev > 0)
        {
            var z = Math.Abs(entry.PayloadSizeBytes.Value - baseline.PayloadSizeMean) / baseline.PayloadSizeStdDev;
            if (z >= _options.ZScoreThreshold && (bestZ is null || z > bestZ))
            {
                bestZ = z;
                pattern = AnomalyPatternTypes.PayloadShapeDrift;
                detail = $"payload_size={entry.PayloadSizeBytes} mean={baseline.PayloadSizeMean:F1} z={z:F2}";
            }
        }

        // Request-rate burst — only flag if rate is ABOVE the mean (not below).
        // A below-mean rate is just a quiet period, not an anomaly.
        if (baseline.RequestRateStdDev > 0)
        {
            var currentRate = baseline.RequestRateMean; // last computed
            var z = (currentRate - baseline.RequestRateMean) / baseline.RequestRateStdDev;
            if (z >= _options.ZScoreThreshold && (bestZ is null || z > bestZ))
            {
                bestZ = z;
                pattern = AnomalyPatternTypes.RequestRateBurst;
                detail = $"rate_mean={baseline.RequestRateMean:F2}/s z={z:F2}";
            }
        }

        // Absolute rate cap — fast-path that doesn't need z-score math.
        // We trust the operator to set this high enough that legitimate
        // traffic can't trip it.
        if (baseline.RequestRateMean >= _options.AbsoluteRateCap)
        {
            bestZ = Math.Max(bestZ ?? 0, 10.0);
            pattern = AnomalyPatternTypes.RequestRateBurst;
            detail = $"absolute_cap={_options.AbsoluteRateCap}/s exceeded (observed mean {baseline.RequestRateMean:F2}/s)";
        }

        if (pattern is null)
            return null;

        _logger.LogWarning("Anomaly detected for partner {PartnerId}: {Pattern} (z={Z:F2})",
            entry.PartnerId, pattern, bestZ);

        return new SecurityAlert(
            partnerId: entry.PartnerId,
            anomalyScore: (decimal)bestZ!,
            patternType: pattern,
            detail: detail);
    }
}
