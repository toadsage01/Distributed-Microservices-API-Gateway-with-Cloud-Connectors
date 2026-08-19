using ApiGateway.Domain.Entities;
using ApiGateway.Domain.ValueObjects;
using Microsoft.Data.SqlClient;

namespace ApiGateway.Infrastructure.Persistence.Repositories;

public interface ISchemaVersionRepository
{
    Task<SchemaVersion?> GetEffectiveAsync(PartnerId partnerId, string? requestedVersion, CancellationToken ct = default);
    Task UpsertAsync(SchemaVersion version, CancellationToken ct = default);
}

public sealed class SchemaVersionRepository : ISchemaVersionRepository
{
    private readonly ISqlConnectionFactory _factory;

    public SchemaVersionRepository(ISqlConnectionFactory factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    /// <summary>
    /// If requestedVersion is non-null, fetch exactly that version (the gateway
    /// validates requests against the partner-declared version). Otherwise,
    /// pick the latest EffectiveDate &lt;= now — the "current" version.
    /// </summary>
    public async Task<SchemaVersion?> GetEffectiveAsync(PartnerId partnerId, string? requestedVersion, CancellationToken ct = default)
    {
        var sql = requestedVersion is null
            ? """
              SELECT TOP 1 Version, JsonSchemaBlob, EffectiveDate
              FROM SchemaVersion
              WHERE PartnerId = @pid AND EffectiveDate <= SYSUTCDATETIME()
              ORDER BY EffectiveDate DESC
              """
            : """
              SELECT TOP 1 Version, JsonSchemaBlob, EffectiveDate
              FROM SchemaVersion
              WHERE PartnerId = @pid AND Version = @ver
              """;

        await using var conn = await _factory.OpenPrimaryAsync(ct).ConfigureAwait(false);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@pid", System.Data.SqlDbType.UniqueIdentifier).Value = partnerId.Value;
        if (requestedVersion is not null)
            cmd.Parameters.Add("@ver", System.Data.SqlDbType.NVarChar, 20).Value = requestedVersion;

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null;

        return SchemaVersion.Rehydrate(
            partnerId: partnerId.Value,
            version: reader.GetString(reader.GetOrdinal("Version")),
            jsonSchemaBlob: reader.GetString(reader.GetOrdinal("JsonSchemaBlob")),
            effectiveDate: reader.GetDateTime(reader.GetOrdinal("EffectiveDate"))
        );
    }

    public async Task UpsertAsync(SchemaVersion version, CancellationToken ct = default)
    {
        const string sql = """
            MERGE SchemaVersion WITH (HOLDLOCK) AS target
            USING (SELECT @pid AS PartnerId, @ver AS Version) AS source
            ON target.PartnerId = source.PartnerId AND target.Version = source.Version
            WHEN MATCHED THEN UPDATE SET
                JsonSchemaBlob = @blob,
                EffectiveDate = @effective
            WHEN NOT MATCHED THEN INSERT
                (PartnerId, Version, JsonSchemaBlob, EffectiveDate)
                VALUES
                (@pid, @ver, @blob, @effective);
            """;

        await using var conn = await _factory.OpenPrimaryAsync(ct).ConfigureAwait(false);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@pid", System.Data.SqlDbType.UniqueIdentifier).Value = version.PartnerId.Value;
        cmd.Parameters.Add("@ver", System.Data.SqlDbType.NVarChar, 20).Value = version.Version;
        cmd.Parameters.Add("@blob", System.Data.SqlDbType.NVarChar).Value = version.JsonSchemaBlob;
        cmd.Parameters.Add("@effective", System.Data.SqlDbType.DateTime2).Value = version.EffectiveDate;

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
