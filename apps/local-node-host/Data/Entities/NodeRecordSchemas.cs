using System.Text.Json;

using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Kernel.Schema;

namespace Harborline.Api.LocalNodeHost.Data.Entities;

/// <summary>
/// Ticket 151 (L1418) — the node's BASELINE record-type activation. The first-party record types a
/// node ships with are activated here, at composition, exactly the way a pack's record types are
/// activated at install-activate: their schema is compiled once into
/// <see cref="CompiledSchemaCatalog"/> and the write path only looks the artefact up.
/// </summary>
/// <remarks>
/// A restart re-runs this activation (the compiled form is never persisted); registration is
/// content-addressed, so the same text is the same CID and the same binding.
/// </remarks>
internal static class NodeRecordSchemas
{
    /// <summary>Compiles the baseline record schemas. Called once, when the catalog is composed.</summary>
    internal static void ActivateBaseline(CompiledSchemaCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        Activate(catalog, Health.EntityRoutes.LegalEntitySchema, LegalEntity());
    }

    private static void Activate(CompiledSchemaCatalog catalog, Foundation.Assets.Common.SchemaId name, string text)
    {
        // Composition is synchronous and the registry backend is in-memory, so activation has already
        // completed by the time the ValueTask is returned. The guard makes that a checked invariant
        // instead of a hope: a backend that ever went truly asynchronous fails composition loudly here
        // rather than blocking a thread-pool thread.
        var activation = catalog.ActivateAsync(name, text);
        if (!activation.IsCompleted)
        {
            throw new InvalidOperationException(
                $"baseline record schema '{name}' did not activate synchronously; composition cannot await it.");
        }

#pragma warning disable VSTHRD002 // completed ValueTask: observing the result cannot block (guard above)
        activation.GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
    }

    private static string LegalEntity() =>
        $$"""
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "required": ["legalName", "kind", "taxClassification"],
          "properties": {
            "legalName": { "type": "string", "minLength": 1, "maxLength": 200 },
            "kind": { "type": "string", "enum": {{Names<EntityKind>()}} },
            "taxClassification": { "type": "string", "enum": {{Names<TaxClassification>()}} },
            "commonControlGroupId": { "type": ["string", "null"], "maxLength": 200 }
          }
        }
        """;

    // The enum IS the domain constraint; spelling the members out again in JSON would be a second
    // authority that drifts.
    private static string Names<TEnum>() where TEnum : struct, Enum
        => JsonSerializer.Serialize(Enum.GetNames<TEnum>());
}
