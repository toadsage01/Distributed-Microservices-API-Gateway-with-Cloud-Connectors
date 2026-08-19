using ApiGateway.Domain.Entities;
using ApiGateway.Domain.ValueObjects;
using ApiGateway.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ApiGateway.IntegrationTests.Infrastructure;

[Collection("SqlServerCollection")]
public sealed class PartnerConfigRepositoryTests : IAsyncLifetime
{
    private readonly SqlServerFixture _fixture;
    private SqlConnectionFactory? _factory;

    public PartnerConfigRepositoryTests(SqlServerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _factory = new SqlConnectionFactory(
            Microsoft.Extensions.Options.Options.Create(new ApiGateway.Infrastructure.Configuration.DatabaseOptions
            {
                PrimaryConnectionString = _fixture.ConnectionString
            }),
            NullLogger<SqlConnectionFactory>.Instance);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task UpsertThenGetByName_RoundTrips()
    {
        var repo = new PartnerConfigRepository(_factory!);
        var cfg = PartnerConfig.Create(
            partnerName: "acme-test-" + Guid.NewGuid().ToString("N").Substring(0, 8),
            schemaVersion: "1.0.0",
            tier: RateLimitTier.Premium,
            authMode: AuthMode.Jwt,
            transformationRulesetRef: "s3://bucket/ruleset.json");

        await repo.UpsertAsync(cfg);
        var loaded = await repo.GetByNameAsync(cfg.PartnerName);

        loaded.Should().NotBeNull();
        loaded!.PartnerName.Should().Be(cfg.PartnerName);
        loaded.SchemaVersion.Should().Be("1.0.0");
        loaded.RateLimitTier.Should().Be(RateLimitTier.Premium);
        loaded.AuthMode.Should().Be(AuthMode.Jwt);
        loaded.TransformationRulesetRef.Should().Be("s3://bucket/ruleset.json");
        loaded.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task GetByName_UnknownPartner_ReturnsNull()
    {
        var repo = new PartnerConfigRepository(_factory!);
        var loaded = await repo.GetByNameAsync("nonexistent-" + Guid.NewGuid());
        loaded.Should().BeNull();
    }
}

[Collection("SqlServerCollection")]
public sealed class IdempotencyKeyRepositoryTests : IAsyncLifetime
{
    private readonly SqlServerFixture _fixture;
    private SqlConnectionFactory? _factory;

    public IdempotencyKeyRepositoryTests(SqlServerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _factory = new SqlConnectionFactory(
            Microsoft.Extensions.Options.Options.Create(new ApiGateway.Infrastructure.Configuration.DatabaseOptions
            {
                PrimaryConnectionString = _fixture.ConnectionString
            }),
            NullLogger<SqlConnectionFactory>.Instance);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task StoreThenTryGet_ReturnsCachedResponse()
    {
        var repo = new IdempotencyKeyRepository(_factory!);
        var partnerId = Guid.NewGuid();
        var key = "idem-" + Guid.NewGuid();
        var hash = "abc123";
        var corrId = CorrelationId.New();

        await repo.StoreAsync(partnerId, key, hash, 200, """{"ok":true}""", corrId, TimeSpan.FromHours(1));

        var cached = await repo.TryGetAsync(partnerId, key, hash);
        cached.Should().NotBeNull();
        cached!.StatusCode.Should().Be(200);
        cached.ResponsePayload.Should().Contain("ok");
    }

    [Fact]
    public async Task TryGet_DifferentRequestBodyHash_ReturnsNull()
    {
        var repo = new IdempotencyKeyRepository(_factory!);
        var partnerId = Guid.NewGuid();
        var key = "idem-" + Guid.NewGuid();
        var corrId = CorrelationId.New();

        await repo.StoreAsync(partnerId, key, "hash-a", 200, """{"ok":true}""", corrId, TimeSpan.FromHours(1));

        // Same partner + key, different request hash — should not match
        var cached = await repo.TryGetAsync(partnerId, key, "hash-b");
        cached.Should().BeNull();
    }
}
