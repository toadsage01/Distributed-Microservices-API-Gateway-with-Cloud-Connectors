using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using ApiGateway.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace ApiGateway.Infrastructure.Secrets;

/// <summary>
/// AWS Secrets Manager wrapper with per-process caching. Cache TTL is 10
/// minutes by default — secrets in this stack (JWT signing key, DB
/// credentials) rotate at most monthly, so aggressive caching is fine.
///
/// On a cache miss we call GetSecretValueAsync. The AWS SDK handles
/// retries and region resolution; we don't layer Polly on top because the
/// SDK already does that with sensible defaults.
/// </summary>
public sealed class SecretsManagerClient : ISecretsProvider, IDisposable
{
    private readonly IAmazonSecretsManager _client;
    private readonly SecretsOptions _options;
    private readonly ILogger<SecretsManagerClient> _logger;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    private readonly Dictionary<string, (string Value, DateTime ExpiresAt)> _stringCache = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    public SecretsManagerClient(
        IOptions<SecretsOptions> options,
        ILogger<SecretsManagerClient> logger)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var config = new AmazonSecretsManagerConfig
        {
            RegionEndpoint = Amazon.RegionEndpoint.GetByRegionSystemName(_options.Region)
        };
        _client = new AmazonSecretsManagerClient(config);
    }

    public async Task<string> GetSecretStringAsync(string secretName, CancellationToken ct = default)
    {
        var (hit, cached) = TryGetCachedString(secretName);
        if (hit) return cached;

        await _cacheLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            (hit, cached) = TryGetCachedString(secretName);
            if (hit) return cached;

            var response = await _client.GetSecretValueAsync(new GetSecretValueRequest
            {
                SecretId = secretName
            }, ct).ConfigureAwait(false);

            var value = response.SecretString ?? throw new InvalidOperationException(
                $"Secret {secretName} returned no SecretString");

            _stringCache[secretName] = (value, DateTime.UtcNow + CacheTtl);
            _logger.LogDebug("Loaded secret {SecretName} from Secrets Manager", secretName);
            return value;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    public async Task<T> GetSecretJsonAsync<T>(string secretName, CancellationToken ct = default)
    {
        var raw = await GetSecretStringAsync(secretName, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(raw) ?? throw new InvalidOperationException(
            $"Secret {secretName} deserialised to null for type {typeof(T).Name}");
    }

    private (bool hit, string value) TryGetCachedString(string name)
    {
        if (_stringCache.TryGetValue(name, out var entry) && entry.ExpiresAt > DateTime.UtcNow)
            return (true, entry.Value);
        return (false, string.Empty);
    }

    public void Dispose() => _client.Dispose();
}
