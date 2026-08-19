using ApiGateway.Domain.Exceptions;
using ApiGateway.Domain.ValueObjects;

namespace ApiGateway.Domain.Entities;

/// <summary>
/// Partner routing and policy configuration. Loaded once per request from the
/// PartnerConfig table and used to drive schema validation, rate-limit tier,
/// and the upstream transformation ruleset reference.
///
/// Mutation goes through the builder methods rather than public setters so the
/// domain can reject invalid transitions (e.g. deactivating a partner whose
/// active sagas are still in-flight is the application layer's call to make,
/// but flipping SchemaVersion to null isn't).
/// </summary>
public class PartnerConfig
{
    public PartnerId PartnerId { get; private set; }
    public string PartnerName { get; private set; }
    public string SchemaVersion { get; private set; }
    public string? TransformationRulesetRef { get; private set; }
    public RateLimitTier RateLimitTier { get; private set; }
    public AuthMode AuthMode { get; private set; }
    public bool IsActive { get; private set; }
    public DateTime CreatedAt { get; private set; }

    private PartnerConfig(
        PartnerId partnerId,
        string partnerName,
        string schemaVersion,
        RateLimitTier tier,
        AuthMode authMode)
    {
        if (string.IsNullOrWhiteSpace(partnerName))
            throw new PartnerConfigurationException("PartnerName is required");
        if (string.IsNullOrWhiteSpace(schemaVersion))
            throw new PartnerConfigurationException("SchemaVersion is required");

        PartnerId = partnerId;
        PartnerName = partnerName;
        SchemaVersion = schemaVersion;
        RateLimitTier = tier;
        AuthMode = authMode;
        IsActive = true;
        CreatedAt = DateTime.UtcNow;
    }

    public static PartnerConfig Create(
        string partnerName,
        string schemaVersion,
        RateLimitTier tier,
        AuthMode authMode,
        string? transformationRulesetRef = null)
    {
        var cfg = new PartnerConfig(PartnerId.New(), partnerName, schemaVersion, tier, authMode)
        {
            TransformationRulesetRef = transformationRulesetRef
        };
        return cfg;
    }

    /// <summary>Rehydrates a PartnerConfig from persistence. Bypasses invariants.</summary>
    public static PartnerConfig Rehydrate(
        Guid partnerId,
        string partnerName,
        string schemaVersion,
        string? transformationRulesetRef,
        RateLimitTier tier,
        AuthMode authMode,
        bool isActive,
        DateTime createdAt)
    {
        return new PartnerConfig(new PartnerId(partnerId), partnerName, schemaVersion, tier, authMode)
        {
            TransformationRulesetRef = transformationRulesetRef,
            IsActive = isActive,
            CreatedAt = createdAt
        };
    }

    public void Deactivate()
    {
        // The application layer must refuse new traffic via gateway middleware
        // (IsActive=false is honoured there); we don't fail here even if a saga
        // is in flight because the alternative — orphaning in-flight work — is worse.
        IsActive = false;
    }

    public void BumpSchemaVersion(string newVersion)
    {
        if (string.IsNullOrWhiteSpace(newVersion))
            throw new PartnerConfigurationException("SchemaVersion cannot be empty");
        SchemaVersion = newVersion;
    }

    public void AssignTransformationRuleset(string? rulesetRef)
    {
        TransformationRulesetRef = rulesetRef;
    }
}
