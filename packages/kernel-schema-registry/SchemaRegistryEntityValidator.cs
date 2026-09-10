using System.Collections.Concurrent;
using System.Text.Json;

using Corvus.Json;
using Corvus.Json.Validator;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;

namespace Harborline.Api.Kernel.Schema;

/// <summary>
/// Authority-side entity validator backed by the schema registry.
/// </summary>
/// <remarks>
/// A Corvus validator is compiled lazily, rather than at pack activation: the registry
/// exposes no activation event and is also used by non-pack callers. The cache key includes
/// both the public id and the registered content address, so a registry implementation which
/// re-activates a logical id with replacement content cannot reuse an obsolete compiled schema.
/// </remarks>
public sealed class SchemaRegistryEntityValidator(ISchemaRegistry schemas) : IEntityValidator
{
    private readonly ConcurrentDictionary<ValidatorCacheKey, JsonSchema> _compiled = new();

    /// <inheritdoc />
    public async Task ValidateAsync(SchemaId schemaId, JsonDocument body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        var schema = await schemas.GetAsync(schemaId, ct).ConfigureAwait(false);
        if (schema is null)
        {
            throw new EntityValidationException(
                "entity.validation.schema_unknown",
                [string.Empty],
                $"Schema '{schemaId.Value}' is not registered.");
        }

        var cacheKey = new ValidatorCacheKey(schema.Id, schema.ContentAddress.Value);
        JsonSchema compiled;
        try
        {
            compiled = _compiled.GetOrAdd(cacheKey, _ => JsonSchema.FromText(
                schema.JsonSchemaText,
                schema.Id.Value,
                JsonSchema.Options.Default,
                false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new EntityValidationException(
                "entity.validation.schema_invalid",
                [string.Empty],
                $"Schema '{schemaId.Value}' could not be compiled for validation.", ex);
        }

        var result = compiled.Validate(body.RootElement, ValidationLevel.Detailed);
        if (result.IsValid)
        {
            return;
        }

        var pointers = result.Results
            .Where(error => !error.Valid)
            .Select(error => ToPointer(error.Location))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        throw new EntityValidationException(
            "entity.validation.body_invalid",
            pointers.Length == 0 ? [string.Empty] : pointers,
            "The entity body does not conform to its registered JSON Schema.");
    }

    private static string ToPointer((JsonReference Schema, JsonReference Instance, JsonReference Validation)? location)
    {
        var value = location?.Instance.ToString() ?? string.Empty;
        if (value.Length == 0 || value[0] == '/')
        {
            return value;
        }

        var fragment = value.IndexOf('#');
        return fragment >= 0 ? value[(fragment + 1)..] : string.Empty;
    }

    private readonly record struct ValidatorCacheKey(SchemaId SchemaId, string ContentAddress);
}
