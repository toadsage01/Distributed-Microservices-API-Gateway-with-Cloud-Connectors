namespace ApiGateway.Domain.Entities;

/// <summary>
/// Catalog of security event types written to AuditLog_Security. Centralised
/// so the trigger's "more than N in W seconds" filter and the sync audit
/// writer stay in sync — typos on string literals here are caught by the
/// compiler, not at incident time.
/// </summary>
public static class SecurityEventTypes
{
    public const string AuthFailure = "AUTH_FAILURE";
    public const string RateLimitBreached = "RATE_LIMIT_BREACHED";
    public const string SchemaValidationRejected = "SCHEMA_VALIDATION_REJECTED";
    public const string UpstreamUnauthorized = "UPSTREAM_UNAUTHORIZED";
    public const string SagaCompensationFailed = "SAGA_COMPENSATION_FAILED";
    public const string IdempotencyKeyReplay = "IDEMPOTENCY_KEY_REPLAY";
}

public static class AnomalyPatternTypes
{
    public const string RequestRateBurst = "request_rate_burst";
    public const string PayloadShapeDrift = "payload_shape_drift";
    public const string EndpointAccessPatternDrift = "endpoint_access_pattern_drift";
    public const string AuthFailureBurst = "auth_failure_burst";
    public const string ManualFastPath = "trigger_fast_path";
}
