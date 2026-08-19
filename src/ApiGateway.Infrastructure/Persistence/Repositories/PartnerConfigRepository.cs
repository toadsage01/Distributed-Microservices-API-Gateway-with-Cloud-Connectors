using ApiGateway.Domain.Entities;
using ApiGateway.Domain.ValueObjects;
using ApiGateway.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace ApiGateway.Infrastructure.Persistence.Repositories;

public interface IPartnerConfigRepository
{
    Task<PartnerConfig?> GetByNameAsync(string partnerName, CancellationToken ct = default);
    Task<PartnerConfig?> GetByIdAsync(PartnerId partnerId, CancellationToken ct = default);
    Task UpsertAsync(PartnerConfig config, CancellationToken ct = default);
}

public sealed class PartnerConfigRepository : IPartnerConfigRepository
{
    private readonly ISqlConnectionFactory _factory;

    public PartnerConfigRepository(ISqlConnectionFactory factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public async Task<PartnerConfig?> GetByNameAsync(string partnerName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(partnerName))
            return null;

        const string sql = """
            SELECT TOP 1
                PartnerId, PartnerName, SchemaVersion,
                TransformationRulesetRef, RateLimitTier, AuthMode, IsActive, CreatedAt
            FROM PartnerConfig
            WHERE PartnerName = @name
            ORDER BY IsActive DESC, CreatedAt DESC
            """;

        await using var conn = await _factory.OpenPrimaryAsync(ct).ConfigureAwait(false);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@name", System.Data.SqlDbType.NVarChar, 200).Value = partnerName;

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? MapRow(reader) : null;
    }

    public async Task<PartnerConfig?> GetByIdAsync(PartnerId partnerId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT TOP 1
                PartnerId, PartnerName, SchemaVersion,
                TransformationRulesetRef, RateLimitTier, AuthMode, IsActive, CreatedAt
            FROM PartnerConfig
            WHERE PartnerId = @id
            """;

        await using var conn = await _factory.OpenPrimaryAsync(ct).ConfigureAwait(false);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@id", System.Data.SqlDbType.UniqueIdentifier).Value = partnerId.Value;

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? MapRow(reader) : null;
    }

    public async Task UpsertAsync(PartnerConfig config, CancellationToken ct = default)
    {
        const string sql = """
            MERGE PartnerConfig WITH (HOLDLOCK) AS target
            USING (SELECT @partnerId AS PartnerId) AS source
            ON target.PartnerId = source.PartnerId
            WHEN MATCHED THEN UPDATE SET
                PartnerName = @name,
                SchemaVersion = @schemaVersion,
                TransformationRulesetRef = @ruleset,
                RateLimitTier = @tier,
                AuthMode = @authMode,
                IsActive = @active
            WHEN NOT MATCHED THEN INSERT
                (PartnerId, PartnerName, SchemaVersion, TransformationRulesetRef, RateLimitTier, AuthMode, IsActive)
                VALUES
                (@partnerId, @name, @schemaVersion, @ruleset, @tier, @authMode, @active);
            """;

        await using var conn = await _factory.OpenPrimaryAsync(ct).ConfigureAwait(false);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@partnerId", System.Data.SqlDbType.UniqueIdentifier).Value = config.PartnerId.Value;
        cmd.Parameters.Add("@name", System.Data.SqlDbType.NVarChar, 200).Value = config.PartnerName;
        cmd.Parameters.Add("@schemaVersion", System.Data.SqlDbType.NVarChar, 20).Value = config.SchemaVersion;
        cmd.Parameters.Add("@ruleset", System.Data.SqlDbType.NVarChar, 500).Value = (object?)config.TransformationRulesetRef ?? DBNull.Value;
        cmd.Parameters.Add("@tier", System.Data.SqlDbType.NVarChar, 50).Value = config.RateLimitTier.ToString().ToLowerInvariant();
        cmd.Parameters.Add("@authMode", System.Data.SqlDbType.NVarChar, 50).Value = AuthModeParser.ToDbValue(config.AuthMode);
        cmd.Parameters.Add("@active", System.Data.SqlDbType.Bit).Value = config.IsActive ? 1 : 0;

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static PartnerConfig MapRow(SqlDataReader reader)
    {
        return PartnerConfig.Rehydrate(
            partnerId: reader.GetGuid(reader.GetOrdinal("PartnerId")),
            partnerName: reader.GetString(reader.GetOrdinal("PartnerName")),
            schemaVersion: reader.GetString(reader.GetOrdinal("SchemaVersion")),
            transformationRulesetRef: reader.IsDBNull(reader.GetOrdinal("TransformationRulesetRef")) ? null : reader.GetString(reader.GetOrdinal("TransformationRulesetRef")),
            tier: Enum.Parse<RateLimitTier>(reader.GetString(reader.GetOrdinal("RateLimitTier")), ignoreCase: true),
            authMode: AuthModeParser.Parse(reader.GetString(reader.GetOrdinal("AuthMode"))),
            isActive: reader.GetBoolean(reader.GetOrdinal("IsActive")),
            createdAt: reader.GetDateTime(reader.GetOrdinal("CreatedAt"))
        );
    }
}
