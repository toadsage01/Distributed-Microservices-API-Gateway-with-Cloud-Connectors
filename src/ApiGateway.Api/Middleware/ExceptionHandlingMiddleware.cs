using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ApiGateway.Api.Middleware;

/// <summary>
/// Top-of-pipeline exception handler. Catches anything that escapes
/// the request pipeline and converts it into an RFC 7807 problem
/// details response. The controller and middleware are expected to
/// handle their own domain errors — this is the catch-all for truly
/// unexpected failures.
/// </summary>
public sealed class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IHostEnvironment _env;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;

    public ExceptionHandlingMiddleware(
        RequestDelegate next,
        IHostEnvironment env,
        ILogger<ExceptionHandlingMiddleware> logger)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _env = env ?? throw new ArgumentNullException(nameof(env));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception on {Method} {Path}", context.Request.Method, context.Request.Path);

            context.Response.StatusCode = 500;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Type = "https://api.gateway/errors/internal",
                Title = "Internal server error",
                Status = 500,
                Detail = _env.IsDevelopment() ? ex.Message : "An unexpected error occurred",
                Instance = context.Request.Path
            }).ConfigureAwait(false);
        }
    }
}
