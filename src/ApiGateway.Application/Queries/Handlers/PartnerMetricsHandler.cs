using ApiGateway.Application.Contracts;
using ApiGateway.Infrastructure.Persistence;
using ApiGateway.Infrastructure.Persistence.Repositories;
using MediatR;
using Microsoft.Data.SqlClient;

namespace ApiGateway.Application.Queries.Handlers;

public sealed class GetPartnerMetricsHandler : IRequestHandler<GetPartnerMetricsQuery, PartnerMetricsSnapshot>
{
    private readonly ISqlConnectionFactory _factory;

    public GetPartnerMetricsHandler(ISqlConnectionFactory factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public async Task<PartnerMetricsSnapshot> Handle(GetPartnerMetricsQuery request, CancellationToken ct)
    {
        // Percentile calc via SQL Server's PERCENTILE_CONT. p99 over a
        // rolling window is the metric most teams alert on first — we
        // compute both p50 and p99 in one round trip.
        const string sql = """
            SELECT
                COUNT(*) AS TotalRequests,
                ISNULL(PERCENTILE_CONT(0.50) WITHIN GROUP (ORDER BY LatencyMs), 0) AS P50,
                ISNULL(PERCENTILE_CONT(0.99) WITHIN GROUP (ORDER BY LatencyMs), 0) AS P99,
                ISNULL(SUM(CASE WHEN StatusCode >= 500 THEN 1 ELSE 0 END) * 1.0 / NULLIF(COUNT(*), 0), 0) AS ErrorRate
            FROM AuditLog_Metrics
            WHERE OccurredAt >= @from AND OccurredAt < @to
              AND (@partnerId IS NULL OR PartnerId = @partnerId)
            """;

        await using var conn = await _factory.OpenReadReplicaAsync(ct).ConfigureAwait(false);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@partnerId", System.Data.SqlDbType.UniqueIdentifier).Value = (object?)request.PartnerId ?? DBNull.Value;
        cmd.Parameters.Add("@from", System.Data.SqlDbType.DateTime2).Value = request.From;
        cmd.Parameters.Add("@to", System.Data.SqlDbType.DateTime2).Value = request.To;

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return new PartnerMetricsSnapshot
            {
                PartnerId = request.PartnerId,
                WindowStart = request.From,
                WindowEnd = request.To
            };
        }

        return new PartnerMetricsSnapshot
        {
            PartnerId = request.PartnerId,
            TotalRequests = reader.GetInt64(reader.GetOrdinal("TotalRequests")),
            P50LatencyMs = reader.GetDouble(reader.GetOrdinal("P50")),
            P99LatencyMs = reader.GetDouble(reader.GetOrdinal("P99")),
            ErrorRate = reader.GetDouble(reader.GetOrdinal("ErrorRate")),
            WindowStart = request.From,
            WindowEnd = request.To
        };
    }
}

public sealed class GetSecurityAlertsHandler : IRequestHandler<GetSecurityAlertsQuery, IReadOnlyList<SecurityAlertSnapshot>>
{
    private readonly ISecurityAlertRepository _repo;

    public GetSecurityAlertsHandler(ISecurityAlertRepository repo)
    {
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
    }

    public async Task<IReadOnlyList<SecurityAlertSnapshot>> Handle(GetSecurityAlertsQuery request, CancellationToken ct)
    {
        var alerts = await _repo.GetRecentAsync(request.PartnerId, request.Limit, ct).ConfigureAwait(false);
        return alerts.Select(a => new SecurityAlertSnapshot
        {
            Id = a.Id,
            PartnerId = a.PartnerId,
            AnomalyScore = a.AnomalyScore,
            PatternType = a.PatternType,
            Detail = a.Detail,
            RaisedAt = a.RaisedAt
        }).ToList();
    }
}
