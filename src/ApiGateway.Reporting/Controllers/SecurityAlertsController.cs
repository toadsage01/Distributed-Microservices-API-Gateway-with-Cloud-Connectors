using ApiGateway.Application.Queries;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ApiGateway.Reporting.Controllers;

[ApiController]
[Route("reporting/alerts")]
[Authorize]
public sealed class SecurityAlertsController : ControllerBase
{
    private readonly IMediator _mediator;

    public SecurityAlertsController(IMediator mediator) => _mediator = mediator;

    [HttpGet]
    public async Task<IActionResult> GetAlerts(
        [FromQuery] Guid? partnerId,
        [FromQuery] int limit = 50,
        CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 500);
        var alerts = await _mediator.Send(new GetSecurityAlertsQuery(partnerId, limit), ct).ConfigureAwait(false);
        return Ok(alerts);
    }
}
