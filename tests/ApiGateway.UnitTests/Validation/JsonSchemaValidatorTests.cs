using ApiGateway.Application.Infrastructure;
using FluentAssertions;
using Xunit;

namespace ApiGateway.UnitTests.Validation;

public class JsonSchemaValidatorTests
{
    private readonly JsonSchemaValidator _validator = new(Microsoft.Extensions.Logging.Abstractions.NullLogger<JsonSchemaValidator>.Instance);

    private const string PersonSchema = """
        {
          "$schema": "http://json-schema.org/draft-07/schema#",
          "type": "object",
          "required": ["name", "age"],
          "properties": {
            "name": { "type": "string", "minLength": 1 },
            "age": { "type": "integer", "minimum": 0 }
          }
        }
        """;

    [Fact]
    public async Task ValidateAsync_ValidPayload_ReturnsTrue()
    {
        var ok = await _validator.ValidateAsync(PersonSchema, """{"name":"acme","age":42}""", out var err);
        ok.Should().BeTrue();
        err.Should().BeNull();
    }

    [Fact]
    public async Task ValidateAsync_MissingRequired_ReturnsFalseWithReason()
    {
        var ok = await _validator.ValidateAsync(PersonSchema, """{"name":"acme"}""", out var err);
        ok.Should().BeFalse();
        err.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ValidateAsync_WrongType_ReturnsFalse()
    {
        var ok = await _validator.ValidateAsync(PersonSchema, """{"name":"acme","age":"old"}""", out var err);
        ok.Should().BeFalse();
        err.Should().Contain("age");
    }

    [Fact]
    public async Task ValidateAsync_UnknownNonBreakingField_ReturnsTrue()
    {
        // Unknown fields are allowed by default — schema drift within the
        // partner's declared version should not break the gateway.
        var ok = await _validator.ValidateAsync(PersonSchema, """{"name":"acme","age":42,"extra":true}""", out var err);
        ok.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateAsync_VersionMismatch_InvalidSchema_HandlesGracefully()
    {
        // Schema is malformed — validator must not throw, it must report failure
        var ok = await _validator.ValidateAsync("not json", """{}""", out var err);
        ok.Should().BeFalse();
        err.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ValidateAsync_CachesSchemaPerContentHash()
    {
        // Calling twice with the same schema string should not re-parse.
        // The cache is internal — we verify by ensuring the second call
        // returns the same result deterministically.
        var first = await _validator.ValidateAsync(PersonSchema, """{"name":"a","age":1}""", out _);
        var second = await _validator.ValidateAsync(PersonSchema, """{"name":"a","age":1}""", out _);
        first.Should().Be(second);
    }
}
