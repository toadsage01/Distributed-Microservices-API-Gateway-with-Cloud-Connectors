using ApiGateway.Domain.ValueObjects;

namespace ApiGateway.Domain.Entities;

/// <summary>
/// Per-partner JSON schema version registry. The (PartnerId, Version) composite
/// key means partners can have multiple concurrent versions during a rollout —
/// the gateway picks the version whose EffectiveDate is the latest &lt;= now.
/// </summary>
public class SchemaVersion
{
    public PartnerId PartnerId { get; private set; }
    public string Version { get; private set; }
    public string JsonSchemaBlob { get; private set; }
    public DateTime EffectiveDate { get; private set; }

    private SchemaVersion(PartnerId partnerId, string version, string jsonSchemaBlob, DateTime effectiveDate)
    {
        if (string.IsNullOrWhiteSpace(version))
            throw new ArgumentException("Version is required", nameof(version));
        if (string.IsNullOrWhiteSpace(jsonSchemaBlob))
            throw new ArgumentException("JsonSchemaBlob is required", nameof(jsonSchemaBlob));

        PartnerId = partnerId;
        Version = version;
        JsonSchemaBlob = jsonSchemaBlob;
        EffectiveDate = effectiveDate;
    }

    public static SchemaVersion Publish(PartnerId partnerId, string version, string jsonSchemaBlob, DateTime? effectiveDate = null)
    {
        var when = effectiveDate ?? DateTime.UtcNow;
        return new SchemaVersion(partnerId, version, jsonSchemaBlob, when);
    }

    public static SchemaVersion Rehydrate(Guid partnerId, string version, string jsonSchemaBlob, DateTime effectiveDate)
        => new(new PartnerId(partnerId), version, jsonSchemaBlob, effectiveDate);

    public bool IsEffectiveAt(DateTime when) => EffectiveDate <= when;
}
