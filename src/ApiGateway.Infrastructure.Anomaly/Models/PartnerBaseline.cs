namespace ApiGateway.Infrastructure.Anomaly;

/// <summary>
/// Rolling per-partner baseline statistics. Kept in memory between
/// checkpoints — the persistent checkpoint is best-effort, not the
/// source of truth. On cold start the baseline is empty and the scorer
/// refuses to score until MinSampleCount samples accumulate.
/// </summary>
public sealed class PartnerBaseline
{
    public Guid PartnerId { get; }
    public int SampleCount { get; private set; }
    public DateTime WindowStart { get; private set; }
    public DateTime LastSampleAt { get; private set; }

    // Online Welford's algorithm for variance — avoids the two-pass
    // mean+variance computation that would need a sample buffer.
    private double _meanLatency;
    private double _m2Latency;
    private double _meanPayloadSize;
    private double _m2PayloadSize;
    private double _meanRequestRate;
    private double _m2RequestRate;

    private DateTime _lastArrival;
    private readonly object _lock = new();

    public PartnerBaseline(Guid partnerId)
    {
        PartnerId = partnerId;
        WindowStart = DateTime.UtcNow;
        LastSampleAt = DateTime.UtcNow;
    }

    public void Observe(int latencyMs, int? payloadSizeBytes)
    {
        lock (_lock)
        {
            var n = ++SampleCount;
            LastSampleAt = DateTime.UtcNow;

            UpdateWelford(ref _meanLatency, ref _m2Latency, latencyMs, n);
            if (payloadSizeBytes.HasValue)
                UpdateWelford(ref _meanPayloadSize, ref _m2PayloadSize, payloadSizeBytes.Value, n);

            // Request rate is computed as the reciprocal of the inter-arrival
            // time between samples. Over a window this approximates
            // requests-per-second well enough for burst detection.
            if (n > 1)
            {
                var interArrival = (LastSampleAt - _lastArrival).TotalSeconds;
                if (interArrival > 0)
                {
                    var rate = 1.0 / interArrival;
                    UpdateWelford(ref _meanRequestRate, ref _m2RequestRate, rate, n - 1);
                }
            }
            _lastArrival = LastSampleAt;
        }
    }

    public double LatencyMean => _meanLatency;
    public double LatencyStdDev => SampleCount > 1 ? Math.Sqrt(_m2Latency / (SampleCount - 1)) : 0;

    public double PayloadSizeMean => _meanPayloadSize;
    public double PayloadSizeStdDev => SampleCount > 1 ? Math.Sqrt(_m2PayloadSize / (SampleCount - 1)) : 0;

    public double RequestRateMean => _meanRequestRate;
    public double RequestRateStdDev => SampleCount > 1 ? Math.Sqrt(_m2RequestRate / (SampleCount - 1)) : 0;

    private static void UpdateWelford(ref double mean, ref double m2, double sample, int n)
    {
        var delta = sample - mean;
        mean += delta / n;
        var delta2 = sample - mean;
        m2 += delta * delta2;
    }
}
