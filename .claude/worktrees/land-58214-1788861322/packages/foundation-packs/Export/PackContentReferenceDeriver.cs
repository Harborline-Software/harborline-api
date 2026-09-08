using Harborline.Api.Foundation.Packs.Graph;
using Harborline.Api.Foundation.Packs.Model;

namespace Harborline.Api.Foundation.Packs.Export;

/// <summary>
/// The Composer's compose-time derivation of the signed manifest's cross-app content-reference edges
/// (<see cref="PackContentReferenceEdge"/>; app-layer design note §6.3, slice G2). It is the ONE place the
/// compose path parses content bodies to find cross-pack references — it delegates the per-kind body parse to
/// <see cref="PackContentReferenceExtractor"/> (the single body-parse seam G1 introduced; no A4 second
/// parser), then RESOLVES each referenced content key to an owning app and keeps only the CROSS-app edges.
/// </summary>
/// <remarks>
/// <para>
/// <b>Where derivable.</b> A reference is emitted only when its target content key resolves to a KNOWN
/// external app — one of the pack's declared <c>Dependencies[]</c> keys (a content-grain reference implies a
/// pack-level dependency, so the two agree). A reference to a sibling leaf in the SAME pack is intra-app
/// wiring and is dropped (§6.3: "only references whose <c>toPackKey</c> differs from the pack's own key are
/// emitted"). A reference the composer cannot resolve to a declared dependency is left for the author to
/// declare (not guessed) — so a pack that references another app WITHOUT declaring the dependency simply
/// emits no edge (and the graph, for such a pack, falls back to the G1 body-parse — backward-compatible).
/// </para>
/// <para>
/// <b>Namespace resolution.</b> Content keys are app-namespaced (<c>core-records.property</c>,
/// <c>fleet-ops.vehicle</c>) and the design's own §6.3 example uses <c>toPackKey</c> = the key's pack prefix.
/// Because a pack key can itself contain dots (<c>harborline.general</c>), a naive first-dot split is
/// ambiguous — so resolution is a LONGEST dot-boundary prefix match against the KNOWN key set (own key +
/// declared dependencies), never a blind string split.
/// </para>
/// <para>
/// <b>Byte-stable + additive.</b> The result is ordinal-sorted (deterministic signed bytes) and is
/// <c>null</c> (not an empty list) when there are no cross-app edges, so a dependency-free pack's manifest
/// serializes byte-identically to a pre-G2 manifest — the additive/backward-compatible guarantee.
/// </para>
/// </remarks>
public static class PackContentReferenceDeriver
{
    /// <summary>
    /// Derives the cross-app content-reference edges for a pack being composed. Returns <c>null</c> when the
    /// pack declares no cross-app reference (the common case), so the manifest field is omitted.
    /// </summary>
    /// <param name="ownPackKey">The pack key being composed (its own leaves are intra-app, not edges).</param>
    /// <param name="contents">The content sources being packaged (key + kind + parsed body).</param>
    /// <param name="declaredDependencyKeys">The pack's declared single-level dependency keys — the set of
    /// external apps a content reference may resolve into.</param>
    public static IReadOnlyList<PackContentReferenceEdge>? Derive(
        string ownPackKey,
        IReadOnlyList<PackContentSource> contents,
        IEnumerable<string> declaredDependencyKeys)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownPackKey);
        ArgumentNullException.ThrowIfNull(contents);
        ArgumentNullException.ThrowIfNull(declaredDependencyKeys);

        // The keys a reference may resolve to: this pack + every declared dependency. Longest-first so a
        // dotted pack key (harborline.general) wins over a shorter prefix (harborline) on the same target.
        var knownKeys = new List<string> { ownPackKey };
        foreach (var dep in declaredDependencyKeys)
        {
            if (!string.IsNullOrWhiteSpace(dep) && !knownKeys.Contains(dep, StringComparer.Ordinal))
            {
                knownKeys.Add(dep);
            }
        }
        knownKeys.Sort((a, b) => b.Length.CompareTo(a.Length)); // longest first

        var ownContentKeys = new HashSet<string>(contents.Select(c => c.Key), StringComparer.Ordinal);

        var edges = new List<PackContentReferenceEdge>();
        foreach (var item in contents)
        {
            foreach (var reference in PackContentReferenceExtractor.Extract(item.Kind, item.Content))
            {
                // A reference to a leaf in THIS pack is intra-app wiring — never a cross-app edge.
                if (ownContentKeys.Contains(reference.ToContentKey))
                {
                    continue;
                }

                var toPackKey = ResolveOwningPackKey(reference.ToContentKey, knownKeys);
                if (toPackKey is null || string.Equals(toPackKey, ownPackKey, StringComparison.Ordinal))
                {
                    // Unresolvable (the author did not declare the dependency) or resolved to self ⇒ drop.
                    continue;
                }

                edges.Add(new PackContentReferenceEdge(
                    FromContentKey: item.Key,
                    FromKind: item.Kind,
                    ToPackKey: toPackKey,
                    ToContentKey: reference.ToContentKey,
                    ToKind: InferTargetKind(reference.Relation),
                    Relation: reference.Relation));
            }
        }

        if (edges.Count == 0)
        {
            return null; // no cross-app edges ⇒ omit the field ⇒ byte-identical to a pre-G2 manifest.
        }

        edges.Sort(CompareEdge);
        return edges;
    }

    /// <summary>
    /// The longest dot-boundary prefix of <paramref name="contentKey"/> among the known keys, or null. A
    /// known key <c>k</c> owns <c>contentKey</c> iff <c>contentKey == k</c> or <c>contentKey</c> starts with
    /// <c>k + "."</c> — never a bare substring (so <c>fleet</c> does not spuriously own <c>fleet-ops.x</c>).
    /// </summary>
    private static string? ResolveOwningPackKey(string contentKey, IReadOnlyList<string> knownKeysLongestFirst)
    {
        foreach (var key in knownKeysLongestFirst)
        {
            if (string.Equals(contentKey, key, StringComparison.Ordinal)
                || contentKey.StartsWith(key + ".", StringComparison.Ordinal))
            {
                return key;
            }
        }

        return null;
    }

    /// <summary>
    /// The target's kind when the relation determines it (a <see cref="PackFeatureEdgeRelation.ParentOf"/>
    /// definitionally targets a record type), else null — the graph resolves the concrete kind from install
    /// state at read time when the target app is installed.
    /// </summary>
    private static PackContentKind? InferTargetKind(PackFeatureEdgeRelation relation) => relation switch
    {
        PackFeatureEdgeRelation.ParentOf => PackContentKind.AssetTypeDefinition,
        _ => null,
    };

    private static int CompareEdge(PackContentReferenceEdge a, PackContentReferenceEdge b)
    {
        int c = string.CompareOrdinal(a.FromContentKey, b.FromContentKey);
        if (c != 0) return c;
        c = string.CompareOrdinal(a.ToPackKey, b.ToPackKey);
        if (c != 0) return c;
        c = string.CompareOrdinal(a.ToContentKey, b.ToContentKey);
        if (c != 0) return c;
        return ((int)a.Relation).CompareTo((int)b.Relation);
    }
}
