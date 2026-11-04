using System.Text.Json.Nodes;

using Harborline.Api.Foundation.Packs.Model;
using Harborline.Blocks.BuilderDefinitions;
using Harborline.Kernel.SchemaValidation.Records;

namespace Harborline.Api.Foundation.Packs.Graph;

/// <summary>
/// The per-kind body-parse that turns a contributable's canonical JSON into the content KEYS it references
/// (app-layer design note §2.1 edge derivation). This is the ONLY place the graph layer reads a content
/// BODY — the seam the design note calls "the one component that parses bodies" for edges. It is deliberately
/// narrow: it extracts references the pinned per-kind content contracts ACTUALLY carry today
/// (<c>pack-content-schemas-2026-07-06.md</c>), not a speculative schema.
/// </summary>
/// <remarks>
/// <para>
/// <b>G1 vs G2.</b> In G1 the cross-app content edge (class 3) is DERIVED by parsing bodies here. In G2 the
/// Composer emits a signed, declared <c>PackManifest.ContentReferences[]</c> and install validates it
/// fail-closed (design note §6.3), at which point this extractor is REPLACED by reading that declared field
/// — the edge vocabulary (<see cref="PackFeatureEdgeRelation"/>) is already the G2 relation vocabulary, so
/// the graph consumers do not change when the derivation source flips. Until then, this is the single
/// derivation (no second parser — the A4 two-implementations-drift class the collision engine warns about).
/// </para>
/// <para>
/// Legacy asset types carry <c>parentType</c>. Records references use <see cref="ExtractRecords"/>
/// over the producer's typed document and resolve against the verified closure at export and install.
/// The graph reads signed Records edges and skips gated bodies in its legacy fallback.
/// </para>
/// </remarks>
public static class PackContentReferenceExtractor
{
    /// <summary>
    /// Extracts the content-key references a contributable body declares, as (targetContentKey, relation)
    /// pairs. Tolerant: a malformed / absent field yields no reference (never throws) — a body the graph
    /// cannot read simply contributes no edges, exactly as the projector skips a malformed item.
    /// </summary>
    public static IReadOnlyList<PackContentReference> Extract(PackContentKind kind, JsonNode? content)
    {
        if (content is not JsonObject obj)
        {
            return Array.Empty<PackContentReference>();
        }

        return kind switch
        {
            // An asset type's parentType is a reference to another type's content key (type hierarchy).
            PackContentKind.AssetTypeDefinition => ParentTypeReference(obj),

            // Recognized future seams (form binds type; workflow references form) — no pinned pack content
            // contract carries them yet, so emit nothing rather than guess. G2's ContentReferences[] +
            // the Wave-2 form/workflow projection light these up.
            _ => Array.Empty<PackContentReference>(),
        };
    }

    /// <summary>The Records identity, empty for a malformed claim, or null for legacy content.</summary>
    public static string? RecordsId(PackContentKind kind, JsonNode? content)
    {
        if (kind != PackContentKind.AssetTypeDefinition || content is not JsonObject obj)
            return null;
        if (obj.TryGetPropertyValue("record_type_id", out var id))
            return id is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
        return obj["fields"] is JsonArray fields && fields.OfType<JsonObject>().Any(field => field.ContainsKey("reference"))
            ? "" : null;
    }

    /// <summary>Every typed Records reference in source order, including duplicates and self references.</summary>
    public static IEnumerable<(RecordReferenceDefinition Reference, string Pointer)> ExtractRecords(RecordTypeDocument document)
        => document.Fields.Select((field, index) => (field.Reference, Pointer: $"/fields/{index}/reference"))
            .Where(pair => pair.Reference is not null).Select(pair => (pair.Reference!, pair.Pointer));

    private static IReadOnlyList<PackContentReference> ParentTypeReference(JsonObject obj)
    {
        if (obj.TryGetPropertyValue("parentType", out var node)
            && node is JsonValue v
            && v.TryGetValue<string>(out var parent)
            && !string.IsNullOrWhiteSpace(parent))
        {
            return new[] { new PackContentReference(parent.Trim(), PackFeatureEdgeRelation.ParentOf) };
        }

        return Array.Empty<PackContentReference>();
    }
}

/// <summary>One reference a contributable body declares: the target content key + the relation.</summary>
/// <param name="ToContentKey">The referenced content key (resolved to an owning app by the index builder).</param>
/// <param name="Relation">The reference relation.</param>
public sealed record PackContentReference(string ToContentKey, PackFeatureEdgeRelation Relation);
