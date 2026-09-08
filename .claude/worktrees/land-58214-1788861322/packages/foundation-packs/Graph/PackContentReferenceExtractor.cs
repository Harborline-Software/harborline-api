using System.Text.Json.Nodes;

using Harborline.Api.Foundation.Packs.Model;

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
/// <b>Today's real reference.</b> The only cross-item reference the shipped content contracts carry is an
/// <see cref="PackContentKind.AssetTypeDefinition"/>'s <c>parentType</c> (a type-hierarchy edge; the seed
/// projector reads the same field). Form→type bindings and workflow→form references are recognized SEAMS
/// (they arrive with the form/workflow projection Wave-2 + the G2 manifest edge) and intentionally return
/// nothing here rather than guess a not-yet-pinned schema.
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
