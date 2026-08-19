using ApiGateway.Domain.Entities;
using ApiGateway.Domain.Exceptions;
using ApiGateway.Domain.ValueObjects;
using FluentAssertions;
using Xunit;

namespace ApiGateway.UnitTests.Domain;

public class PartnerConfigTests
{
    [Fact]
    public void Create_WithValidArgs_BuildsActivePartner()
    {
        var cfg = PartnerConfig.Create(
            partnerName: "acme",
            schemaVersion: "1.0.0",
            tier: RateLimitTier.Premium,
            authMode: AuthMode.Jwt);

        cfg.PartnerId.Value.Should().NotBeEmpty();
        cfg.PartnerName.Should().Be("acme");
        cfg.SchemaVersion.Should().Be("1.0.0");
        cfg.RateLimitTier.Should().Be(RateLimitTier.Premium);
        cfg.AuthMode.Should().Be(AuthMode.Jwt);
        cfg.IsActive.Should().BeTrue();
        cfg.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithBlankName_Throws(string? name)
    {
        var act = () => PartnerConfig.Create(name!, "1.0.0", RateLimitTier.Standard, AuthMode.Jwt);
        act.Should().Throw<PartnerConfigurationException>();
    }

    [Fact]
    public void BumpSchemaVersion_ReplacesVersion()
    {
        var cfg = PartnerConfig.Create("acme", "1.0.0", RateLimitTier.Standard, AuthMode.Jwt);
        cfg.BumpSchemaVersion("2.0.0");
        cfg.SchemaVersion.Should().Be("2.0.0");
    }

    [Fact]
    public void Deactivate_FlipsIsActive()
    {
        var cfg = PartnerConfig.Create("acme", "1.0.0", RateLimitTier.Standard, AuthMode.Jwt);
        cfg.Deactivate();
        cfg.IsActive.Should().BeFalse();
    }

    [Fact]
    public void Rehydrate_PreservesAllFields()
    {
        var id = Guid.NewGuid();
        var createdAt = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        var cfg = PartnerConfig.Rehydrate(
            partnerId: id,
            partnerName: "legacy",
            schemaVersion: "0.9.0",
            transformationRulesetRef: "s3://bucket/ruleset.json",
            tier: RateLimitTier.Legacy,
            authMode: AuthMode.ApiKey,
            isActive: false,
            createdAt: createdAt);

        cfg.PartnerId.Value.Should().Be(id);
        cfg.PartnerName.Should().Be("legacy");
        cfg.SchemaVersion.Should().Be("0.9.0");
        cfg.TransformationRulesetRef.Should().Be("s3://bucket/ruleset.json");
        cfg.RateLimitTier.Should().Be(RateLimitTier.Legacy);
        cfg.AuthMode.Should().Be(AuthMode.ApiKey);
        cfg.IsActive.Should().BeFalse();
        cfg.CreatedAt.Should().Be(createdAt);
    }
}
