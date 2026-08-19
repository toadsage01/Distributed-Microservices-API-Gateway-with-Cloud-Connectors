using ApiGateway.Api.Middleware;
using ApiGateway.Application;
using ApiGateway.Infrastructure;
using ApiGateway.Infrastructure.Anomaly;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, lc) => lc
    .ReadFrom.Configuration(ctx.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] {CorrelationId} {Message:lj} {Properties:j}{NewLine}{Exception}"));

// Configure options
builder.Services.Configure<ApiGateway.Infrastructure.Configuration.DatabaseOptions>(
    builder.Configuration.GetSection("Database"));
builder.Services.Configure<ApiGateway.Infrastructure.Configuration.AuditOptions>(
    builder.Configuration.GetSection("Audit"));
builder.Services.Configure<ApiGateway.Infrastructure.Configuration.SecretsOptions>(
    builder.Configuration.GetSection("Secrets"));

// Layered DI: infra -> app -> anomaly -> api surface
builder.Services.AddApiGatewayInfrastructure(builder.Configuration);
builder.Services.AddApiGatewayApplication(builder.Configuration);
builder.Services.AddApiGatewayAnomaly(builder.Configuration);

// JWT auth — the middleware handles validation manually, but ASP.NET
// still needs AddAuthentication so [Authorize] works on controllers.
builder.Services.AddAuthentication("Bearer")
    .AddJwtBearer("Bearer", options =>
    {
        options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            // Signature + lifetime are validated manually in JwtAuthMiddleware
            // so we can write the AUTH_FAILURE audit event on rejection.
            ValidateIssuerSigningKey = false,
            ValidateLifetime = false,
            RequireSignedTokens = false
        };
    });
builder.Services.AddAuthorization();

// OpenAPI
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "API Gateway",
        Version = "v1",
        Description = "Distributed microservices API gateway with saga orchestration, multi-partner schema validation, and out-of-band anomaly detection.",
        Contact = new Microsoft.OpenApi.Models.OpenApiContact
        {
            Name = "API Gateway Team"
        }
    });

    var xmlFile = $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
    if (File.Exists(xmlPath)) c.IncludeXmlComments(xmlPath);
});

// OpenTelemetry traces
builder.Services.AddOpenTelemetry()
    .WithTracing(tp => tp
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddSqlClientInstrumentation()
        .AddSource("ApiGateway"));

builder.Services.AddHealthChecks()
    .AddCheck("self", () => Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy(), tags: new[] { "live" });

var app = builder.Build();

// Run pending migrations before serving traffic. Skipped in tests where
// Testcontainers pre-provisioned the schema.
if (builder.Configuration.GetValue<bool?>("Database:RunMigrationsOnStartup") ?? true)
{
    await app.Services.RunMigrationsAsync();
}

// Middleware pipeline — order matters. Exception handler first so it
// catches anything that escapes. CorrelationId next so every log line
// afterwards is annotated. Then auth, rate limit, schema validation.
app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<JwtAuthMiddleware>();
app.UseMiddleware<RateLimitMiddleware>();
app.UseMiddleware<SchemaValidationMiddleware>();

app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapControllers();
app.MapHealthChecks("/health/ready");

app.Run();

public partial class Program { }
