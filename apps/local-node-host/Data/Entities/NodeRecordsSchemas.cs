using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.LocalNodeHost.Health;

namespace Harborline.Api.LocalNodeHost.Data.Entities;

/// <summary>
/// The JSON Schema (draft 2020-12) the node holds for each records type it writes itself
/// (ticket 151, L1418). <see cref="SchemaRegistryEntityValidator"/> registers these into the
/// authority's schema registry on first use; a records write whose schema is in neither this set nor
/// the registry is refused, never passed.
/// </summary>
/// <remarks>
/// A pack-activated Records type needs no row here: its schema is registered by the pack projector
/// and its <c>SchemaId</c> is the registry's own <c>schema:{cid}</c>, which the validator resolves
/// directly. This set exists only for the records types whose shape the node itself declares in code.
/// </remarks>
public static class NodeRecordsSchemas
{
    /// <summary>The legal-entity body shape the node's own entity route and writer mint.</summary>
    /// <remarks>
    /// The enumerations are generated from the domain enums, so a new <see cref="EntityKind"/> or
    /// <see cref="TaxClassification"/> member cannot leave the authority's schema behind the code.
    /// <c>legalName</c> requires a non-whitespace character (the trim the writer applies must leave a
    /// name), which is why it carries a <c>pattern</c> rather than only <c>minLength</c>.
    /// </remarks>
    public static string LegalEntity { get; } = $$"""
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "title": "legal-entity",
          "type": "object",
          "required": ["legalName", "kind", "taxClassification"],
          "properties": {
            "legalName": { "type": "string", "minLength": 1, "pattern": "\\S" },
            "kind": { "type": "string", "enum": [{{Names<EntityKind>()}}] },
            "taxClassification": { "type": "string", "enum": [{{Names<TaxClassification>()}}] },
            "commonControlGroupId": { "type": ["string", "null"] }
          }
        }
        """;

    /// <summary>The node's records schemas keyed by the <c>SchemaId</c> value the write path carries.</summary>
    public static IReadOnlyDictionary<string, string> All { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [EntityRoutes.LegalEntitySchema.Value] = LegalEntity,
    };

    private static string Names<T>() where T : struct, Enum =>
        string.Join(", ", Enum.GetNames<T>().Select(name => $"\"{name}\""));
}
