namespace ApiGateway.Infrastructure.Configuration;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>Primary connection string — write-path DBO + read-your-own-writes.</summary>
    public string PrimaryConnectionString { get; set; } = string.Empty;

    /// <summary>Read replica — only used by the reporting API. Resolved separately to enforce the read/write split at the connection-factory level.</summary>
    public string ReadReplicaConnectionString { get; set; } = string.Empty;

    /// <summary>Hard cap on connection lifetime in seconds — defends against stale connections after a Multi-AZ failover.</summary>
    public int ConnectionLifetimeSeconds { get; set; } = 300;

    /// <summary>Default command timeout for non-bulk operations.</summary>
    public int DefaultCommandTimeoutSeconds { get; set; } = 30;

    /// <summary>Max retries on transient errors before bubbling. Resilience policies add their own retry on top.</summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>Whether the startup migration runner should block until migrations complete. Disable for tests that pre-provision the schema via Testcontainers.</summary>
    public bool RunMigrationsOnStartup { get; set; } = true;
}

public sealed class AuditOptions
{
    public const string SectionName = "Audit";

    /// <summary>Sync audit writes go here. Falls back to file queue on DB unavailability.</summary>
    public string FallbackQueuePath { get; set; } = "/var/lib/apigateway/audit-fallback";

    /// <summary>Async batch writer flush threshold — whichever of this or FlushIntervalMs fires first triggers a bulk write.</summary>
    public int BatchSize { get; set; } = 500;

    /// <summary>Async batch writer flush interval in milliseconds.</summary>
    public int FlushIntervalMs { get; set; } = 250;

    /// <summary>Maximum number of telemetry records buffered before backpressure is applied to the channel.</summary>
    public int ChannelCapacity { get; set; } = 10_000;

    /// <summary>How long the fallback-recovery BackgroundService waits between drains.</summary>
    public int FallbackRecoveryIntervalSeconds { get; set; } = 30;
}

public sealed class SecretsOptions
{
    public const string SectionName = "Secrets";

    /// <summary>AWS region for Secrets Manager calls.</summary>
    public string Region { get; set; } = "us-east-1";

    /// <summary>Secret ARN or name containing the JWT signing key material.</summary>
    public string JwtSigningKeySecretName { get; set; } = "apigateway/jwt-signing-key";

    /// <summary>Secret ARN or name containing the DB credentials (host, user, password).</summary>
    public string DbCredentialsSecretName { get; set; } = "apigateway/db-credentials";

    /// <summary>Whether to actually call Secrets Manager — false in dev where the connection string is in appsettings.</summary>
    public bool Enabled { get; set; } = false;
}
