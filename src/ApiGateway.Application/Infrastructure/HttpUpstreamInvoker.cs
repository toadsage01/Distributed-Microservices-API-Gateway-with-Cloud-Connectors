using ApiGateway.Application.Abstractions;
using ApiGateway.Application.Contracts;
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using System.Text;

namespace ApiGateway.Application.Infrastructure;

/// <summary>
/// HttpClient-based upstream invoker. The HttpClient itself is injected
/// by IHttpClientFactory and pre-configured with Polly resilience
/// handlers (retry, circuit breaker, timeout) via the AddUpstreamInvoker
/// DI extension. We don't add Polly here because resilience policies
/// belong to the registration site, not the typed client.
/// </summary>
public sealed class HttpUpstreamInvoker : IUpstreamInvoker
{
    private readonly HttpClient _client;
    private readonly ILogger<HttpUpstreamInvoker> _logger;

    public HttpUpstreamInvoker(HttpClient client, ILogger<HttpUpstreamInvoker> logger)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<UpstreamResult> InvokeAsync(UpstreamInvocation invocation, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(
                new HttpMethod(invocation.HttpMethod.ToUpperInvariant()),
                invocation.Url);

            if (invocation.Body is not null)
            {
                request.Content = new StringContent(invocation.Body, Encoding.UTF8, "application/json");
            }

            foreach (var (name, value) in invocation.Headers)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }

            using var response = await _client.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            // Treat 2xx as success; 4xx is a business-logic failure (do not
            // retry); 5xx is treated as a failure here but the Polly retry
            // policy in front of the HttpClient may already have tried
            // multiple times before we got here.
            var success = (int)response.StatusCode is >= 200 and < 300;

            return new UpstreamResult
            {
                Success = success,
                StatusCode = (int)response.StatusCode,
                Body = body,
                Headers = response.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value)),
                FailureReason = success ? null : $"HTTP {response.StatusCode}"
            };
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Upstream {Upstream} timed out", invocation.UpstreamName);
            return new UpstreamResult
            {
                Success = false,
                StatusCode = 504,
                FailureReason = $"upstream timeout: {ex.Message}"
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Upstream {Upstream} call failed", invocation.UpstreamName);
            return new UpstreamResult
            {
                Success = false,
                StatusCode = 502,
                FailureReason = $"upstream call failed: {ex.Message}"
            };
        }
    }
}
