using ApiGateway.Infrastructure.Audit;
using ApiGateway.Infrastructure.Configuration;
using ApiGateway.Infrastructure.Migration;
using ApiGateway.Infrastructure.Persistence;
using ApiGateway.Infrastructure.Persistence.Repositories;
using ApiGateway.Infrastructure.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ApiGateway.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddApiGatewayInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<DatabaseOptions>(config.GetSection(DatabaseOptions.SectionName));
        services.Configure<AuditOptions>(config.GetSection(AuditOptions.SectionName));
        services.Configure<SecretsOptions>(config.GetSection(SecretsOptions.SectionName));

        services.AddSingleton<ISqlConnectionFactory, SqlConnectionFactory>();
        services.AddSingleton<IAuditFallbackQueue, AuditFallbackFileQueue>();
        services.AddSingleton<IAuditMetricsChannel, ChannelAuditMetricsChannel>();
        services.AddSingleton<ISyncAuditWriter, SyncAuditWriter>();
        services.AddSingleton<IAsyncAuditWriter, ChannelAsyncAuditWriter>();

        services.AddScoped<IPartnerConfigRepository, PartnerConfigRepository>();
        services.AddScoped<ISagaExecutionLogRepository, SagaExecutionLogRepository>();
        services.AddScoped<ISchemaVersionRepository, SchemaVersionRepository>();
        services.AddScoped<IIdempotencyKeyRepository, IdempotencyKeyRepository>();
        services.AddScoped<ISecurityAlertRepository, SecurityAlertRepository>();

        services.AddHostedService<AsyncAuditBatchWriter>();
        services.AddHostedService<AuditFallbackRecoveryService>();

        // The SecretsManagerClient uses AWS SDK credentials resolved from the
        // environment — in dev we use NullSecretsProvider so tests don't need
        // AWS credentials to run.
        var secretsEnabled = config.GetSection(SecretsOptions.SectionName).GetValue<bool?>("Enabled") ?? false;
        if (secretsEnabled)
        {
            services.AddSingleton<ISecretsProvider, SecretsManagerClient>();
        }
        else
        {
            services.AddSingleton<ISecretsProvider, NullSecretsProvider>();
        }

        services.AddSingleton<MigrationRunner>();

        return services;
    }

    /// <summary>
    /// Runs pending migrations on startup. Call this from the API host's
    /// Program.cs after building the host but before RunAsync. Tests skip
    /// this — they pre-provision the schema via Testcontainers.
    /// </summary>
    public static async Task RunMigrationsAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<MigrationRunner>();
        await runner.RunAllAsync(ct).ConfigureAwait(false);
    }
}
