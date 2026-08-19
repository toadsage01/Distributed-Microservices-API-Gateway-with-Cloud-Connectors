using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ApiGateway.Infrastructure.Anomaly;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddApiGatewayAnomaly(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<AnomalyOptions>(config.GetSection(AnomalyOptions.SectionName));

        services.AddSingleton<IPartnerBaselineStore, InMemoryPartnerBaselineStore>();
        services.AddSingleton<ISecurityAlertWriter, SecurityAlertWriter>();
        services.AddSingleton<IAnomalyScorer, StatisticalScorer>();
        services.AddHostedService<AnomalyDetectionService>();

        return services;
    }
}
