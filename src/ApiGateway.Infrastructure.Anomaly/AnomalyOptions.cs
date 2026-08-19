namespace ApiGateway.Infrastructure.Anomaly;

public sealed class AnomalyOptions
{
    public const string SectionName = "Anomaly";

    /// <summary>Z-score threshold beyond which a deviation becomes an alert. 3.0 is "3 sigma".</summary>
    public double ZScoreThreshold { get; set; } = 3.0;

    /// <summary>Minimum sample count before we start scoring — below this the baseline is too noisy.</summary>
    public int MinSampleCount { get; set; } = 30;

    /// <summary>Rolling window for baseline calculation. Defaults to 1 hour.</summary>
    public int BaselineWindowSeconds { get; set; } = 3600;

    /// <summary>How often the baseline checkpoint is persisted to the DB.</summary>
    public int CheckpointIntervalSeconds { get; set; } = 300;

    /// <summary>Maximum requests-per-second per partner before we fast-path an alert regardless of z-score.</summary>
    public int AbsoluteRateCap { get; set; } = 50;
}
