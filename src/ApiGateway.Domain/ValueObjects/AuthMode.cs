namespace ApiGateway.Domain.ValueObjects;

public enum AuthMode
{
    Jwt,
    OAuth2ClientCredentials,
    ApiKey
}

public static class AuthModeParser
{
    public static AuthMode Parse(string raw)
    {
        return raw.Trim().ToLowerInvariant() switch
        {
            "jwt" => AuthMode.Jwt,
            "oauth2" or "oauth2-client-credentials" => AuthMode.OAuth2ClientCredentials,
            "apikey" or "api-key" => AuthMode.ApiKey,
            _ => throw new ArgumentException($"Unknown auth mode '{raw}'", nameof(raw))
        };
    }

    public static string ToDbValue(AuthMode mode) => mode switch
    {
        AuthMode.Jwt => "jwt",
        AuthMode.OAuth2ClientCredentials => "oauth2",
        AuthMode.ApiKey => "apikey",
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };
}
