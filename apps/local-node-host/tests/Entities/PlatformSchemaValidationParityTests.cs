using System.Text;

using Harborline.Api.Kernel.Schema;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// T-303: the api registry validates through the platform <c>Harborline.Kernel.SchemaValidation</c>
/// library. Each case is one where the retired api validator and the platform library disagreed;
/// the platform behaviour is the contract every Forms and record consumer now receives.
/// </summary>
public sealed class PlatformSchemaValidationParityTests
{
    private static InMemorySchemaRegistry NewRegistry() => new(TimeProvider.System);

    [Fact]
    public async Task A_schema_declaring_a_foreign_dialect_is_refused_at_registration()
    {
        // The api copy documented draft 2020-12 only but registered a draft-07 declaration.
        await Assert.ThrowsAsync<InvalidSchemaException>(async () => await NewRegistry().RegisterAsync(
            """{ "$schema": "http://json-schema.org/draft-07/schema#", "type": "string" }"""));
    }

    [Fact]
    public async Task A_malformed_document_is_an_invalid_json_refusal_not_an_exception()
    {
        var registry = NewRegistry();
        var schema = await registry.RegisterAsync("""{ "type": "string" }""");

        var result = await registry.ValidateAsync(schema.Id, Encoding.UTF8.GetBytes("{nope"));

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Equal(string.Empty, error.JsonPointer);
        Assert.Equal("invalid-json", error.Code);
    }

    [Theory]
    [InlineData("""{ "type": "object", "properties": { "a": {} }, "additionalProperties": false }""", """{ "a": 1, "b": 2 }""", "/b", "additional-properties")]
    [InlineData("""{ "type": "object", "additionalProperties": { "type": "string" } }""", """{ "b": 2 }""", "/b", "type")]
    [InlineData("""{ "type": "object", "properties": { "a": {} }, "unevaluatedProperties": false }""", """{ "a": 1, "b": 2 }""", "/b", "additional-properties")]
    [InlineData("""{ "type": "object", "propertyNames": { "maxLength": 2 } }""", """{ "abc": 1 }""", "/abc", "maxLength")]
    [InlineData("""{ "type": "array", "contains": { "type": "string" } }""", "[1]", "/0", "type")]
    public async Task One_failure_under_an_applicator_is_one_field_addressed_error(
        string schemaText, string document, string pointer, string code)
    {
        // The api copy also emitted the applicator's own summary at the document root, so a
        // per-field renderer showed a second, form-level error for the same failure.
        var registry = NewRegistry();
        var schema = await registry.RegisterAsync(schemaText);

        var result = await registry.ValidateAsync(schema.Id, Encoding.UTF8.GetBytes(document));

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Equal(pointer, error.JsonPointer);
        Assert.Equal(code, error.Code);
    }
}
