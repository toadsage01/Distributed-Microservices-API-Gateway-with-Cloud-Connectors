namespace ApiGateway.Domain.Entities;

/// <summary>
/// Security-relevant audit records written on the synchronous path: auth
/// failures, rate-limit breaches, schema-validation rejections. Volume is
/// low (these only fire on rejection events), so a trigger on this table
/// can afford to fan out a notification for auth-failure bursts.
/// </summary>
public class AuditLogSecurity
{
    public long Id { get; init; }
    public string EventType { get; init; }
    public Guid? PartnerId { get; init; }
    public string? Detail { get; init; }
    public DateTime OccurredAt { get; init; }

    public AuditLogSecurity(string eventType, Guid? partnerId, string? detail, DateTime? occurredAt = null)
    {
        if (string.IsNullOrWhiteSpace(eventType))
            throw new ArgumentException("EventType is required", nameof(eventType));

        EventType = eventType;
        PartnerId = partnerId;
        Detail = detail;
        OccurredAt = occurredAt ?? DateTime.UtcNow;
    }
}

/// <summary>
/// High-volume request telemetry written via Channel&lt;T&gt; → SqlBulkCopy.
/// Partitioning by OccurredAt (monthly) lets the archival job swap out old
/// partitions without locking the hot insert path.
/// </summary>
public class AuditLogMetrics
{
    public long Id { get; init; }
    public Guid? PartnerId { get; init; }
    public string Route { get; init; }
    public int LatencyMs { get; init; }
    public int? PayloadSizeBytes { get; init; }
    public int StatusCode { get; init; }
    public DateTime OccurredAt { get; init; }

    public AuditLogMetrics(Guid? partnerId, string route, int latencyMs, int statusCode, int? payloadSizeBytes = null, DateTime? occurredAt = null)
    {
        if (string.IsNullOrWhiteSpace(route))
            throw new ArgumentException("Route is required", nameof(route));
        if (latencyMs < 0)
            throw new ArgumentOutOfRangeException(nameof(latencyMs));
        if (statusCode < 100 || statusCode > 599)
            throw new ArgumentOutOfRangeException(nameof(statusCode));

        PartnerId = partnerId;
        Route = route;
        LatencyMs = latencyMs;
        StatusCode = statusCode;
        PayloadSizeBytes = payloadSizeBytes;
        OccurredAt = occurredAt ?? DateTime.UtcNow;
    }
}

/// <summary>
/// Output of the anomaly detector. Score is the z-score that tripped the alert
/// (or -1 for the trigger-driven fast path from AuditLog_Security), PatternType
/// labels the kind of deviation (request_rate_burst, payload_shape_drift, etc.).
/// </summary>
public class SecurityAlert
{
    public long Id { get; init; }
    public Guid? PartnerId { get; init; }
    public decimal AnomalyScore { get; init; }
    public string PatternType { get; init; }
    public string? Detail { get; init; }
    public DateTime RaisedAt { get; init; }

    public SecurityAlert(Guid? partnerId, decimal anomalyScore, string patternType, string? detail, DateTime? raisedAt = null)
    {
        if (string.IsNullOrWhiteSpace(patternType))
            throw new ArgumentException("PatternType is required", nameof(patternType));

        PartnerId = partnerId;
        AnomalyScore = anomalyScore;
        PatternType = patternType;
        Detail = detail;
        RaisedAt = raisedAt ?? DateTime.UtcNow;
    }
}
