using ApiGateway.Domain.Entities;
using ApiGateway.Infrastructure.Persistence.Repositories;
using Microsoft.Extensions.Logging;

namespace ApiGateway.Infrastructure.Anomaly;

public sealed class SecurityAlertWriter : ISecurityAlertWriter
{
    private readonly ISecurityAlertRepository _repo;
    private readonly ILogger<SecurityAlertWriter> _logger;

    public SecurityAlertWriter(ISecurityAlertRepository repo, ILogger<SecurityAlertWriter> logger)
    {
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task WriteAsync(SecurityAlert alert, CancellationToken ct = default)
    {
        try
        {
            await _repo.InsertAsync(alert, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Alert writes are best-effort — failure here means we miss
            // one anomaly, not that the request itself failed.
            _logger.LogWarning(ex, "Failed to persist security alert for partner {PartnerId} pattern {Pattern}",
                alert.PartnerId, alert.PatternType);
        }
    }
}
