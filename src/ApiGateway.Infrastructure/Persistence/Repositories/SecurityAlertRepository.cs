using ApiGateway.Domain.Entities;
using Microsoft.Data.SqlClient;

namespace ApiGateway.Infrastructure.Persistence.Repositories;

public interface ISecurityAlertRepository
{
    Task InsertAsync(SecurityAlert alert, CancellationToken ct = default);
    Task<IReadOnlyList<SecurityAlert>> GetRecentAsync(Guid? partnerId, int limit, CancellationToken ct = default);
}

public sealed class SecurityAlertRepository : ISecurityAlertRepository
{
    private readonly ISqlConnectionFactory _factory;

    public SecurityAlertRepository(ISqlConnectionFactory factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public async Task InsertAsync(SecurityAlert alert, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO SecurityAlert
                (PartnerId, AnomalyScore, PatternType, Detail, RaisedAt)
            VALUES
                (@pid, @score, @pattern, @detail, @raised);
            """;

        await using var conn = await _factory.OpenPrimaryAsync(ct).ConfigureAwait(false);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@pid", System.Data.SqlDbType.UniqueIdentifier).Value = (object?)alert.PartnerId ?? DBNull.Value;
        cmd.Parameters.Add("@score", System.Data.SqlDbType.Decimal).Value = alert.AnomalyScore;
        cmd.Parameters.Add("@pattern", System.Data.SqlDbType.NVarChar, 100).Value = alert.PatternType;
        cmd.Parameters.Add("@detail", System.Data.SqlDbType.NVarChar).Value = (object?)alert.Detail ?? DBNull.Value;
        cmd.Parameters.Add("@raised", System.Data.SqlDbType.DateTime2).Value = alert.RaisedAt;

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SecurityAlert>> GetRecentAsync(Guid? partnerId, int limit, CancellationToken ct = default)
    {
        var sql = partnerId is null
            ? """
              SELECT TOP (@limit) Id, PartnerId, AnomalyScore, PatternType, Detail, RaisedAt
              FROM SecurityAlert
              ORDER BY RaisedAt DESC
              """
            : """
              SELECT TOP (@limit) Id, PartnerId, AnomalyScore, PatternType, Detail, RaisedAt
              FROM SecurityAlert
              WHERE PartnerId = @pid
              ORDER BY RaisedAt DESC
              """;

        await using var conn = await _factory.OpenPrimaryAsync(ct).ConfigureAwait(false);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@limit", System.Data.SqlDbType.Int).Value = limit;
        if (partnerId is not null)
            cmd.Parameters.Add("@pid", System.Data.SqlDbType.UniqueIdentifier).Value = partnerId;

        var results = new List<SecurityAlert>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(new SecurityAlert(
                partnerId: reader.IsDBNull(reader.GetOrdinal("PartnerId")) ? null : reader.GetGuid(reader.GetOrdinal("PartnerId")),
                anomalyScore: reader.GetDecimal(reader.GetOrdinal("AnomalyScore")),
                patternType: reader.GetString(reader.GetOrdinal("PatternType")),
                detail: reader.IsDBNull(reader.GetOrdinal("Detail")) ? null : reader.GetString(reader.GetOrdinal("Detail")),
                raisedAt: reader.GetDateTime(reader.GetOrdinal("RaisedAt"))
            ));
        }
        return results;
    }
}
