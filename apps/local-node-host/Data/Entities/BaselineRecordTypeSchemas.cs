using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.LocalNodeHost.Data.Entities;

/// <summary>
/// JSON Schema (draft 2020-12) for the record types the node itself ships, as opposed to the ones
/// that arrive in a pack and are registered with <c>ISchemaRegistry.RegisterAsync</c> at activation.
/// The authority-side validator (ticket 151) resolves a pack-activated record type from the registry
/// and falls back to this map for a baseline type.
/// </summary>
internal static class BaselineRecordTypeSchemas
{
    /// <summary>The legal-entity body the entity route writes (<c>EntityRoutes.LegalEntitySchema</c>).</summary>
    internal const string LegalEntity = """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "title": "Harborline legal entity",
          "type": "object",
          "required": ["legalName"],
          "additionalProperties": false,
          "properties": {
            "legalName": { "type": "string", "minLength": 1, "maxLength": 200 },
            "kind": { "type": ["string", "null"] },
            "taxClassification": { "type": ["string", "null"] },
            "commonControlGroupId": { "type": ["string", "null"], "maxLength": 128 }
          }
        }
        """;

    /// <summary>The baseline map handed to the validator at composition.</summary>
    internal static IReadOnlyDictionary<SchemaId, string> All { get; } =
        new Dictionary<SchemaId, string>
        {
            [Health.EntityRoutes.LegalEntitySchema] = LegalEntity,
        };
}
