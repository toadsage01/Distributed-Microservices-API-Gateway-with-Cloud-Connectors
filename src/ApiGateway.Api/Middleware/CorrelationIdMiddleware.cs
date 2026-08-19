using ApiGateway.Application.Common;
using ApiGateway.Domain.ValueObjects;
using Microsoft.AspNetCore.Http;

namespace ApiGateway.Api.Middleware;

/// <summary>
/// Reads X-Correlation-Id from the request (or generates one) and
/// publishes it via the AsyncLocal CorrelationContext so the rest of
/// the pipeline can pick it up without threading it through every
/// signature. Also echoes the correlation id back as a response header
/// so clients can trace a request through our logs.
/// </summary>
public sealed class CorrelationIdMiddleware
{
    private readonly RequestDelegate _next;
    public const string HeaderName = "X-Correlation-Id";
    public const string ResponseHeaderName = "X-Correlation-Id";

    public CorrelationIdMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, ICorrelationContextAccessor correlationAccessor)
    {
        var incoming = context.Request.Headers[HeaderName].ToString();
        var correlation = string.IsNullOrWhiteSpace(incoming)
            ? CorrelationId.New()
            : CorrelationId.Parse(incoming);

        correlationAccessor.Current = correlation;
        context.Items["CorrelationId"] = correlation.Value;

        context.Response.Headers[ResponseHeaderName] = correlation.Value.ToString("D");

        await _next(context).ConfigureAwait(false);
    }
}
