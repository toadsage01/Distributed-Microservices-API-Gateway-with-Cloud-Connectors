using ApiGateway.Domain.Entities;
using ApiGateway.Domain.ValueObjects;
using Microsoft.Data.SqlClient;

namespace ApiGateway.Infrastructure.Persistence.Repositories;

public sealed class SagaStepRecord
{
    public Guid Id { get; init; }
    public string StepName { get; init; } = string.Empty;
    public int StepOrder { get; init; }
    public SagaState State { get; init; }
    public string? PayloadSnapshot { get; init; }
}

public interface ISagaExecutionLogRepository
{
    Task InsertAsync(SagaExecutionLog entry, CancellationToken ct = default);
    Task UpdateStateAsync(Guid id, SagaState newState, string? payloadSnapshot, CancellationToken ct = default);

    /// <summary>Reverse-ordered completed/failed steps for a saga — what sp_CompensateSaga returns.</summary>
    Task<IReadOnlyList<SagaStepRecord>> GetCompensatableStepsAsync(CorrelationId correlationId, CancellationToken ct = default);

    /// <summary>Sagas whose steps are stuck in non-terminal states past the staleness threshold — used by the recovery runner.</summary>
    Task<IReadOnlyList<Guid>> GetStaleSagaStepIdsAsync(TimeSpan staleness, CancellationToken ct = default);
}

public sealed class SagaExecutionLogRepository : ISagaExecutionLogRepository
{
    private readonly ISqlConnectionFactory _factory;

    public SagaExecutionLogRepository(ISqlConnectionFactory factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public async Task InsertAsync(SagaExecutionLog entry, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO SagaExecutionLog
                (Id, CorrelationId, StepName, StepOrder, State, PayloadSnapshot, CreatedAt, UpdatedAt)
            VALUES
                (@id, @corr, @step, @order, @state, @payload, @created, @updated)
            """;

        await using var conn = await _factory.OpenPrimaryAsync(ct).ConfigureAwait(false);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@id", System.Data.SqlDbType.UniqueIdentifier).Value = entry.Id;
        cmd.Parameters.Add("@corr", System.Data.SqlDbType.UniqueIdentifier).Value = entry.CorrelationId.Value;
        cmd.Parameters.Add("@step", System.Data.SqlDbType.NVarChar, 200).Value = entry.StepName;
        cmd.Parameters.Add("@order", System.Data.SqlDbType.Int).Value = entry.StepOrder;
        cmd.Parameters.Add("@state", System.Data.SqlDbType.NVarChar, 30).Value = entry.State.ToString().ToLowerInvariant();
        cmd.Parameters.Add("@payload", System.Data.SqlDbType.NVarChar).Value = (object?)entry.PayloadSnapshot ?? DBNull.Value;
        cmd.Parameters.Add("@created", System.Data.SqlDbType.DateTime2).Value = entry.CreatedAt;
        cmd.Parameters.Add("@updated", System.Data.SqlDbType.DateTime2).Value = entry.UpdatedAt;

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task UpdateStateAsync(Guid id, SagaState newState, string? payloadSnapshot, CancellationToken ct = default)
    {
        var sql = payloadSnapshot is null
            ? "UPDATE SagaExecutionLog SET State = @state, UpdatedAt = SYSUTCDATETIME() WHERE Id = @id"
            : "UPDATE SagaExecutionLog SET State = @state, PayloadSnapshot = @payload, UpdatedAt = SYSUTCDATETIME() WHERE Id = @id";

        await using var conn = await _factory.OpenPrimaryAsync(ct).ConfigureAwait(false);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@state", System.Data.SqlDbType.NVarChar, 30).Value = newState.ToString().ToLowerInvariant();
        cmd.Parameters.Add("@id", System.Data.SqlDbType.UniqueIdentifier).Value = id;
        if (payloadSnapshot is not null)
            cmd.Parameters.Add("@payload", System.Data.SqlDbType.NVarChar).Value = payloadSnapshot;

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SagaStepRecord>> GetCompensatableStepsAsync(CorrelationId correlationId, CancellationToken ct = default)
    {
        const string sql = "EXEC dbo.sp_CompensateSaga @CorrelationId";

        await using var conn = await _factory.OpenPrimaryAsync(ct).ConfigureAwait(false);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@CorrelationId", System.Data.SqlDbType.UniqueIdentifier).Value = correlationId.Value;

        var results = new List<SagaStepRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(new SagaStepRecord
            {
                Id = reader.GetGuid(reader.GetOrdinal("Id")),
                StepName = reader.GetString(reader.GetOrdinal("StepName")),
                StepOrder = reader.GetInt32(reader.GetOrdinal("StepOrder")),
                State = Enum.Parse<SagaState>(reader.GetString(reader.GetOrdinal("State")), ignoreCase: true),
                PayloadSnapshot = reader.IsDBNull(reader.GetOrdinal("PayloadSnapshot")) ? null : reader.GetString(reader.GetOrdinal("PayloadSnapshot"))
            });
        }
        return results;
    }

    public async Task<IReadOnlyList<Guid>> GetStaleSagaStepIdsAsync(TimeSpan staleness, CancellationToken ct = default)
    {
        const string sql = """
            SELECT Id
            FROM SagaExecutionLog
            WHERE State IN ('pending','running','compensating')
              AND UpdatedAt < DATEADD(second, -@stalenessSeconds, SYSUTCDATETIME())
            """;

        await using var conn = await _factory.OpenPrimaryAsync(ct).ConfigureAwait(false);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@stalenessSeconds", System.Data.SqlDbType.Int).Value = (int)staleness.TotalSeconds;

        var results = new List<Guid>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(reader.GetGuid(0));
        }
        return results;
    }
}
