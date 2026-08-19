using Microsoft.AspNetCore.Mvc;

namespace ApiGateway.Api.Controllers;

[ApiController]
[Route("health")]
public sealed class HealthController : ControllerBase
{
    private readonly ILogger<HealthController> _logger;

    public HealthController(ILogger<HealthController> logger) => _logger = logger;

    [HttpGet("live")]
    public IActionResult Live() => Ok(new { status = "healthy", timestamp = DateTime.UtcNow });

    [HttpGet("ready")]
    public async Task<IActionResult> ReadyAsync([FromServices] IServiceProvider sp)
    {
        try
        {
            // Probe the database by attempting to open the primary connection.
            // If the connection fails, the readiness probe returns 503 and the
            // ALB pulls the target out of rotation.
            var factory = sp.GetRequiredService<ApiGateway.Infrastructure.Persistence.ISqlConnectionFactory>();
            await using var conn = await factory.OpenPrimaryAsync(HttpContext.RequestAborted);
            return Ok(new { status = "ready", database = "reachable", timestamp = DateTime.UtcNow });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Readiness probe failed");
            return StatusCode(503, new { status = "unavailable", database = "unreachable", timestamp = DateTime.UtcNow });
        }
    }
}
