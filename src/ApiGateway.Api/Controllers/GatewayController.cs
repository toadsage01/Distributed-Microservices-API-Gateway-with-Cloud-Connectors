using ApiGateway.Application.Commands;
using ApiGateway.Application.Contracts;
using ApiGateway.Domain.ValueObjects;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ApiGateway.Api.Controllers;

/// <summary>
/// Catch-all gateway controller. Routes inbound requests to the saga
/// pipeline via the ForwardPartnerRequest MediatR command.
///
/// The {partner} segment is the registered PartnerName; the {**route}
/// segment is opaque to the gateway — the upstream invoker uses it to
/// build the upstream URL via the partner's TransformationRulesetRef.
/// </summary>
[ApiController]
[Route("v1/{partner}/{**route}")]
[Authorize]
public sealed class GatewayController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly ILogger<GatewayController> _logger;

    public GatewayController(IMediator mediator, ILogger<GatewayController> logger)
    {
        _mediator = mediator ?? throw new ArgumentNullException(nameof(mediator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    private async Task<IActionResult> ForwardAsync(CancellationToken ct)
    {
        var partner = RouteData.Values["partner"]?.ToString();
        var route = RouteData.Values["route"]?.ToString() ?? string.Empty;
        var partnerName = partner ?? string.Empty;

        if (string.IsNullOrWhiteSpace(partnerName))
        {
            return BadRequest(new ProblemDetails
            {
                Type = "https://api.gateway/errors/missing-partner",
                Title = "Missing partner",
                Status = 400,
                Detail = "The partner segment is required in the URL path"
            });
        }

        string? body = null;
        if (Request.ContentLength is > 0)
        {
            Request.EnableBuffering();
            using var reader = new StreamReader(Request.Body);
            body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            Request.Body.Position = 0;
        }

        var headers = Request.Headers
            .Where(h => !h.Key.StartsWith("Authorization", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(h => h.Key, h => h.Value.ToString());

        var correlationId = HttpContext.Items["CorrelationId"] is Guid g
            ? new CorrelationId(g)
            : CorrelationId.New();

        var request = new GatewayRequest
        {
            PartnerName = partnerName,
            Route = route,
            SchemaVersion = Request.Headers["X-Schema-Version"].ToString(),
            IdempotencyKey = Request.Headers["Idempotency-Key"].ToString(),
            HttpMethod = Request.Method,
            Body = body,
            ContentType = Request.ContentType ?? "application/json",
            Headers = headers,
            CorrelationId = correlationId
        };

        var response = await _mediator.Send(new ForwardPartnerRequestCommand(request), ct).ConfigureAwait(false);

        // Echo correlation id back on every response
        Response.Headers["X-Correlation-Id"] = correlationId.Value.ToString("D");

        // Surface idempotency cache hit so the client knows the response is from cache
        if (response.IsFromIdempotencyCache)
        {
            Response.Headers["X-Idempotency-Replay"] = "true";
        }

        return StatusCode(response.StatusCode, response.Body);
    }

    [HttpPost]
    public Task<IActionResult> PostAsync(CancellationToken ct) => ForwardAsync(ct);

    [HttpPut]
    public Task<IActionResult> PutAsync(CancellationToken ct) => ForwardAsync(ct);

    [HttpPatch]
    public Task<IActionResult> PatchAsync(CancellationToken ct) => ForwardAsync(ct);

    [HttpGet]
    public Task<IActionResult> GetAsync(CancellationToken ct) => ForwardAsync(ct);

    [HttpDelete]
    public Task<IActionResult> DeleteAsync(CancellationToken ct) => ForwardAsync(ct);
}
