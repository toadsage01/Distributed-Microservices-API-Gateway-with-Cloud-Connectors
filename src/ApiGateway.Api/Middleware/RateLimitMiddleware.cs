using System.Collections.Concurrent;
using ApiGateway.Application.Abstractions;
using ApiGateway.Domain.Entities;
using ApiGateway.Infrastructure.Audit;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace ApiGateway.Api.Middleware;

/// <summary>
/// Per-partner, in-process sliding-window rate limiter. Each active
/// partner has a RateLimitWindow with a counter that resets when the
/// window ages out. Limits come from RateLimitTierExtensions.
///
/// Out-of-process rate limiting (e.g. Redis-backed) is out of scope for
/// v1 — single-instance is fine for the expected throughput, and the
/// ALB-level rate limit on the AWS side catches burst attacks before
/// they reach the app.
/// </summary>
public sealed class RateLimitMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ISyncAuditWriter _audit;
    private readonly ILogger<RateLimitMiddleware> _logger;
    private readonly ConcurrentDictionary<Guid, RateLimitWindow> _windows = new();

    public RateLimitMiddleware(
        RequestDelegate next,
        ISyncAuditWriter audit,
        ILogger<RateLimitMiddleware> logger)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task InvokeAsync(HttpContext context, IPartnerConfigLoader loader)
    {
        if (context.Request.Path.StartsWithSegments("/health"))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var partnerName = context.Items["PartnerName"] as string;
        if (string.IsNullOrWhiteSpace(partnerName))
        {
            // Should never happen — JwtAuthMiddleware rejects unauthenticated
            // requests before this runs. Defensive return-412 here.
            context.Response.StatusCode = 412;
            return;
        }

        var partner = await loader.LoadAsync(partnerName, context.RequestAborted).ConfigureAwait(false);
        if (partner is null)
        {
            context.Response.StatusCode = 401;
            return;
        }

        var window = _windows.GetOrAdd(partner.PartnerId.Value, _ => new RateLimitWindow());
        var limit = partner.RateLimitTier.RequestsPerMinute();
        var burst = partner.RateLimitTier.BurstCapacity();

        if (!window.TryConsume(limit, burst))
        {
            await _audit.WriteAsync(new AuditLogSecurity(
                eventType: SecurityEventTypes.RateLimitBreached,
                partnerId: partner.PartnerId.Value,
                detail: $"partner={partnerName} tier={partner.RateLimitTier} limit={limit}/min"), context.RequestAborted).ConfigureAwait(false);

            context.Response.StatusCode = 429;
            context.Response.Headers["Retry-After"] = "60";
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new
            {
                type = "https://api.gateway/errors/rate-limited",
                title = "Rate limit exceeded",
                status = 429,
                detail = $"Partner rate limit of {limit}/min exceeded"
            }).ConfigureAwait(false);
            return;
        }

        // Stash partner context for downstream middleware
        context.Items["PartnerId"] = partner.PartnerId.Value;
        context.Items["PartnerRateLimitTier"] = partner.RateLimitTier;

        await _next(context).ConfigureAwait(false);
    }

    private sealed class RateLimitWindow
    {
        private readonly object _lock = new();
        private DateTime _windowStart = DateTime.UtcNow;
        private int _count;
        private int _burst;

        public bool TryConsume(int limitPerMinute, int burstCapacity)
        {
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                if (now - _windowStart >= TimeSpan.FromMinutes(1))
                {
                    _windowStart = now;
                    _count = 0;
                    _burst = 0;
                }

                if (_count >= limitPerMinute) return false;
                if (_burst >= burstCapacity)
                {
                    // Allow the rate to refill at the steady-state rate
                    var refill = (int)((now - _windowStart).TotalSeconds * limitPerMinute / 60.0);
                    _burst = Math.Max(0, _burst - refill);
                    if (_burst >= burstCapacity) return false;
                }

                _count++;
                _burst++;
                return true;
            }
        }
    }
}
