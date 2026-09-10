using System.Text;
using System.Text.Json;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Kernel.Schema;

namespace Harborline.Api.LocalNodeHost.Data.Entities;

/// <summary>
/// Host authority validator that delegates record-body validation to the activated schema registry.
/// </summary>
public sealed class SchemaRegistryEntityValidator(ISchemaRegistry schemas) : IEntityValidator
{
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
                "The record schema is not active on this node.",
                "entity.validation.schema_unknown",
                [string.Empty],
                ex);
        }

        if (!result.IsValid)
        {
            var pointers = result.Errors
                .Select(error => error.JsonPointer)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            throw new EntityValidationException(
                "The record body does not satisfy its active schema.",
                "entity.validation.body_invalid",
                pointers);
        }
    }
}

/// <summary>Registers the built-in legal-entity record schema in the same registry that validates it.</summary>
public sealed class NodeEntitySchemaCatalog
{
    private const string LegalEntitySchemaDocument = """
        {
          "type": "object",
          "required": ["legalName", "kind", "taxClassification"],
          "properties": {
            "legalName": { "type": "string", "minLength": 1 },
            "kind": { "type": "string", "enum": ["SoleProprietorship", "Partnership", "Llc", "Corporation"] },
            "taxClassification": { "type": "string", "enum": ["DisregardedEntity", "Partnership", "SCorporation", "CCorporation", "SoleProprietorship"] },
            "commonControlGroupId": { "type": ["string", "null"] }
          }
        }
        """;

    /// <summary>Activates the built-in schema during host composition.</summary>
    public NodeEntitySchemaCatalog(ISchemaRegistry schemas)
    {
        ArgumentNullException.ThrowIfNull(schemas);
        LegalEntity = schemas.RegisterAsync(LegalEntitySchemaDocument).AsTask().GetAwaiter().GetResult().Id;
    }

    /// <summary>The active schema identity for legal-entity records.</summary>
    public SchemaId LegalEntity { get; }
}
