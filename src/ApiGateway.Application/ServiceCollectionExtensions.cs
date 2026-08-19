using ApiGateway.Application.Abstractions;
using ApiGateway.Application.Commands.Handlers;
using ApiGateway.Application.Infrastructure;
using ApiGateway.Application.Sagas;
using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ApiGateway.Application;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddApiGatewayApplication(this IServiceCollection services, IConfiguration config)
    {
        services.AddMediatR(cfg =>
        {
            cfg.RegisterServicesFromAssembly(typeof(ServiceCollectionExtensions).Assembly);
            cfg.Lifetime = ServiceLifetime.Scoped;
        });

        services.AddSingleton<ICorrelationContextAccessor, Common.CorrelationContext>();
        services.AddSingleton<IPartnerConfigLoader, PartnerConfigLoader>();
        services.AddSingleton<ISchemaValidator, JsonSchemaValidator>();
        services.AddSingleton<IPayloadTransformer, JsonPathPayloadTransformer>();
        services.AddSingleton<IIdempotencyKeyResolver, IdempotencyKeyResolver>();

        services.AddHttpClient<IUpstreamInvoker, HttpUpstreamInvoker>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        // Saga orchestrator + recovery service
        services.AddSingleton<SagaOrchestrator>();
        services.AddHostedService<SagaRecoveryService>();

        return services;
    }
}
