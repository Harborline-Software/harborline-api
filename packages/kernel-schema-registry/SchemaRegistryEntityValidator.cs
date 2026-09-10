using System.Text;
using System.Text.Json;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;

namespace Harborline.Api.Kernel.Schema;

/// <summary>
/// Authority-side entity validator backed by the registry's compiled JSON Schema artefacts.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="InMemorySchemaRegistry.RegisterAsync"/> parses each schema once and stores the parsed
/// JsonSchema beside its content-addressed <see cref="Schema"/>. This adapter only evaluates that
/// artefact; it never parses or compiles a schema on a write path.
/// </para>
/// <para>
/// A missing registry entry, including a backend that has not restored its compiled artefact after
/// restart, is a named refusal. A persistent registry must rebuild its compiled artefacts while loading
/// schemas, before it admits writes.
/// </para>
/// </remarks>
public sealed class SchemaRegistryEntityValidator(ISchemaRegistry schemas) : IEntityValidator
{
    /// <summary>Stable refusal when an entity schema is not available for evaluation.</summary>
    public const string SchemaUnknownCode = "entity.validation.schema_unknown";

    /// <summary>Stable refusal when a registered schema rejects a body.</summary>
    public const string BodyInvalidCode = "entity.validation.body_invalid";

    private readonly ISchemaRegistry _schemas = schemas ?? throw new ArgumentNullException(nameof(schemas));

    /// <inheritdoc />
    public async Task ValidateAsync(SchemaId schema, JsonDocument body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);

        SchemaValidationResult result;
        try
        {
            result = await _schemas.ValidateAsync(
                schema,
                Encoding.UTF8.GetBytes(body.RootElement.GetRawText()),
                ct).ConfigureAwait(false);
        }
        catch (SchemaNotFoundException ex)
        {
            throw new EntityValidationException(
                SchemaUnknownCode,
                [string.Empty],
                $"Entity schema '{schema.Value}' is not registered for authority validation.",
                ex);
        }

        if (result.IsValid)
            return;

        var pointers = result.Errors
            .Select(error => error.JsonPointer)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        throw new EntityValidationException(
            BodyInvalidCode,
            pointers,
            $"Entity body failed authority validation at {string.Join(", ", pointers)}.");
    }
}
