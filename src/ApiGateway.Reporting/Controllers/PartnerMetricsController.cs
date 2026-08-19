using ApiGateway.Application.Queries;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ApiGateway.Reporting.Controllers;

/// <summary>
/// Read-only metrics endpoint. Hits the read replica connection string
/// resolved via SqlConnectionFactory.OpenReadReplicaAsync — the
/// connection factory enforces the read/write split, not the controller.
/// </summary>
[ApiController]
[Route("reporting/metrics")]
[Authorize]
public sealed class PartnerMetricsController : ControllerBase
{
    private readonly IMediator _mediator;

    public PartnerMetricsController(IMediator mediator) => _mediator = mediator;

    [HttpGet("partner/{partnerId}")]
    public async Task<IActionResult> GetPartner(
        Guid partnerId,
        [FromQuery] DateTime from,
        [FromQuery] DateTime to,
        CancellationToken ct)
    {
        if (from >= to)
            return BadRequest(new ProblemDetails { Title = "Invalid time window", Status = 400 });

        var snapshot = await _mediator.Send(new GetPartnerMetricsQuery(partnerId, from, to), ct).ConfigureAwait(false);
        return Ok(snapshot);
    }

    [HttpGet("global")]
    public async Task<IActionResult> GetGlobal(
        [FromQuery] DateTime from,
        [FromQuery] DateTime to,
        CancellationToken ct)
    {
        if (from >= to)
            return BadRequest(new ProblemDetails { Title = "Invalid time window", Status = 400 });

        var snapshot = await _mediator.Send(new GetPartnerMetricsQuery(null, from, to), ct).ConfigureAwait(false);
        return Ok(snapshot);
    }
}
