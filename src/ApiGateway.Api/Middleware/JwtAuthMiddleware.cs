using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using ApiGateway.Domain.Entities;
using ApiGateway.Infrastructure.Audit;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace ApiGateway.Api.Middleware;

/// <summary>
/// Validates the bearer token against the JWT signing key resolved from
/// Secrets Manager. On failure writes a sync audit record (EventType =
/// AUTH_FAILURE) so the trigger fast-path can fire on bursts.
///
/// The middleware runs before the rate limiter and schema validator so
/// unauthenticated traffic never reaches the more expensive validation
/// stages. On success, the partner name claim is forwarded to later
/// middleware via HttpContext.Items.
/// </summary>
public sealed class JwtAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IConfiguration _config;
    private readonly ISyncAuditWriter _audit;
    private readonly ILogger<JwtAuthMiddleware> _logger;
    private readonly string _signingKey;

    public JwtAuthMiddleware(
        RequestDelegate next,
        IConfiguration config,
        ISyncAuditWriter audit,
        ILogger<JwtAuthMiddleware> logger)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Dev: signing key is in appsettings. Prod: secrets provider injects
        // it via an IOptions post-configure. Either way we resolve at
        // construction so the middleware runs hot.
        _signingKey = _config["Jwt:SigningKey"]
            ?? throw new InvalidOperationException("Jwt:SigningKey is not configured");
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Health and readiness endpoints bypass auth so the ALB can probe
        // them without credentials.
        if (context.Request.Path.StartsWithSegments("/health"))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var authHeader = context.Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            await RejectAsync(context, 401, "missing_bearer", "Missing or malformed Authorization header").ConfigureAwait(false);
            return;
        }

        var token = authHeader["Bearer ".Length..].Trim();
        try
        {
            var handler = new JwtSecurityTokenHandler();
            var parameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_signingKey)),
                ValidateIssuer = false,
                ValidateAudience = false,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30)
            };

            var principal = handler.ValidateToken(token, parameters, out var securityToken);
            context.User = principal;

            // Stash the partner claim so downstream middleware (rate limiter,
            // schema validator) and the controller don't have to re-parse
            // the token.
            var partnerName = principal.FindFirst("partner")?.Value
                ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            context.Items["PartnerName"] = partnerName;
            context.Items["JwtValidatedAt"] = DateTime.UtcNow;

            await _next(context).ConfigureAwait(false);
        }
        catch (SecurityTokenException ex)
        {
            await RejectAsync(context, 401, "invalid_token", ex.Message).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "JWT validation threw");
            await RejectAsync(context, 500, "auth_error", "Internal error during authentication").ConfigureAwait(false);
        }
    }

    private async Task RejectAsync(HttpContext context, int status, string reason, string detail)
    {
        // Auth failures must hit the sync audit writer so the trigger can
        // detect bursts. We don't have a partner id yet (the token was
        // invalid), so PartnerId is null — the trigger's GROUP BY
        // handles nulls by skipping them.
        await _audit.WriteAsync(new AuditLogSecurity(
            eventType: SecurityEventTypes.AuthFailure,
            partnerId: null,
            detail: $"{reason}: {detail}"), context.RequestAborted).ConfigureAwait(false);

        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new
        {
            type = "https://api.gateway/errors/auth-failure",
            title = "Authentication failed",
            status,
            detail,
            instance = context.Request.Path
        }).ConfigureAwait(false);
    }
}
