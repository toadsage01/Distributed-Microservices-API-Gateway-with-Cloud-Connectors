using ApiGateway.Application.Contracts;
using MediatR;

namespace ApiGateway.Application.Queries;

public sealed record GetPartnerMetricsQuery(Guid? PartnerId, DateTime From, DateTime To) : IRequest<PartnerMetricsSnapshot>;

public sealed record PartnerMetricsSnapshot
{
    public Guid? PartnerId { get; init; }
    public long TotalRequests { get; init; }
    public double P50LatencyMs { get; init; }
    public double P99LatencyMs { get; init; }
    public double ErrorRate { get; init; }
    public DateTime WindowStart { get; init; }
    public DateTime WindowEnd { get; init; }
}

public sealed record GetSecurityAlertsQuery(Guid? PartnerId, int Limit = 50) : IRequest<IReadOnlyList<SecurityAlertSnapshot>>;

public sealed record SecurityAlertSnapshot
{
    public long Id { get; init; }
    public Guid? PartnerId { get; init; }
    public decimal AnomalyScore { get; init; }
    public string PatternType { get; init; } = string.Empty;
    public string? Detail { get; init; }
    public DateTime RaisedAt { get; init; }
}

public sealed record GetAuditSecurityQuery(Guid? PartnerId, string? EventType, DateTime From, DateTime To, int Limit = 100) : IRequest<IReadOnlyList<AuditSecuritySnapshot>>;

public sealed record AuditSecuritySnapshot
{
    public long Id { get; init; }
    public string EventType { get; init; } = string.Empty;
    public Guid? PartnerId { get; init; }
    public string? Detail { get; init; }
    public DateTime OccurredAt { get; init; }
}
