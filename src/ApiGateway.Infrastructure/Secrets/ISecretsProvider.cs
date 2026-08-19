namespace ApiGateway.Infrastructure.Secrets;

/// <summary>
/// Provides secret values for runtime use. Implementations must cache by
/// secret name — Secrets Manager pricing is per-call and we don't want
/// to pay per request to fetch the JWT signing key.
/// </summary>
public interface ISecretsProvider
{
    Task<string> GetSecretStringAsync(string secretName, CancellationToken ct = default);
    Task<T> GetSecretJsonAsync<T>(string secretName, CancellationToken ct = default);
}

/// <summary>
/// Default no-op provider used in development and tests where the actual
/// secrets live in appsettings.json. The infrastructure DI extension
/// swaps this for the real SecretsManagerClient when Secrets:Enabled is
/// true.
/// </summary>
public sealed class NullSecretsProvider : ISecretsProvider
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public void WithSecret(string name, string value) => _values[name] = value;

    public Task<string> GetSecretStringAsync(string secretName, CancellationToken ct = default)
    {
        _values.TryGetValue(secretName, out var v);
        return Task.FromResult(v ?? string.Empty);
    }

    public Task<T> GetSecretJsonAsync<T>(string secretName, CancellationToken ct = default)
    {
        _values.TryGetValue(secretName, out var v);
        return Task.FromResult(System.Text.Json.JsonSerializer.Deserialize<T>(v ?? "{}")!);
    }
}
