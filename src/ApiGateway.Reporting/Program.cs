using Serilog;
using ApiGateway.Application;
using ApiGateway.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, lc) => lc
    .ReadFrom.Configuration(ctx.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console());

// The reporting host shares the same infrastructure + application layers
// as the gateway host. It does NOT register the anomaly or audit-writer
// services — the reporting API never writes audit records.
builder.Services.AddApiGatewayInfrastructure(builder.Configuration);
builder.Services.AddApiGatewayApplication(builder.Configuration);

builder.Services.AddAuthentication("Bearer")
    .AddJwtBearer("Bearer", options =>
    {
        // Reporting API uses the same JWT signing key as the gateway.
        options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateIssuerSigningKey = false,
            ValidateLifetime = false
        };
    });
builder.Services.AddAuthorization();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();

public partial class Program { }
