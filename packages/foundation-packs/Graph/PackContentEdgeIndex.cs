using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Model;

namespace Harborline.Api.Foundation.Packs.Graph;

/// <summary>
/// The rebuildable content-edge-index (app-layer design note §2.2) — the ONLY materialized artifact of the
/// feature graph. It caches the edges that require reading content BODIES (intra-app references + cross-app
/// content references, classes 1 and 3), so the read-model does not re-parse every body per query. It is a
/// CACHE, never a source of truth: it is keyed by <see cref="InstallStateFingerprint"/> (the install-state
/// version it was built for) and can be dropped and rebuilt from install state at any time to yield the
/// identical result. Pack-level dependency edges (class 2) are NOT stored here — they are read straight off
/// <see cref="InstalledPack.Dependencies"/> at read time (no body parse), so keeping them out of the cache
/// keeps the cache's single job (body-derived edges) clean.
/// </summary>
/// <param name="InstallStateFingerprint">The install-state version this index reflects (the cache key).</param>
/// <param name="ContentEdges">The body-derived edges (class 1 + class 3), deterministic order.</param>
public sealed record PackContentEdgeIndex(
    string InstallStateFingerprint,
    IReadOnlyList<PackFeatureGraphEdge> ContentEdges)
{
    /// <summary>The empty index (no install state) — a safe "drop" state to rebuild from.</summary>
    public static PackContentEdgeIndex Empty { get; } =
        new(PackInstallStateFingerprint.Empty, Array.Empty<PackFeatureGraphEdge>());
}

/// <summary>
/// Builds the rebuildable content-edge-index from durable install state — the pure derivation the seed
/// projector invokes as a side-effect of projection (design note §2.2: "the seed projector emits a
/// rebuildable content-edge-index as it projects"). Deterministic + total: the same install state always
/// yields the same index (proven by the drop-and-rebuild test).
/// </summary>
public static class PackContentEdgeIndexBuilder
{
    /// <summary>
    /// Derives the content-reference edges (class 1 intra-app + class 3 cross-app) for the representative
    /// version of every installed pack key. For a G2 pack that carries the signed manifest's DECLARED
    /// <see cref="InstalledPack.ContentReferences"/>, the class-3 edges are read STRAIGHT off those declared
    /// edges — no body re-parse (design note §6.3: "the class-3 edge survives without body parse"). For a
    /// pre-G2 / dependency-free pack (no declared references), the G1 BODY-PARSE fallback runs unchanged, so
    /// such a pack behaves exactly as before (the additive/backward-compatible guarantee). An edge's target is
    /// resolved to its owning app + kind by the global content-key→app map; a body-parsed target no installed
    /// app ships yields a dangling cross-app edge (<see cref="PackFeatureGraphEdge.ToPackKey"/> = null), while
    /// a DECLARED edge always carries the named target app even when not currently installed (the strictly
    /// better G2 seam — the graph can name the missing app for the "requires X" preview).
    /// </summary>
    public static PackContentEdgeIndex Build(IReadOnlyList<InstalledPack> installed)
    {
        ArgumentNullException.ThrowIfNull(installed);

        var representatives = PackFeatureGraphInstallState.Representatives(installed);

        // Global content-key → (owning app, kind). Namespaced keys mean a key usually belongs to exactly one
        // app; a contested key (>1 app ships it, the G6 collision case) resolves deterministically to the
        // ordinal-least owner for target-resolution — ownership adjudication proper is the collision engine's
        // job, not the graph's, and G1 does not surface a contested target specially.
        var owner = new Dictionary<string, (string PackKey, PackContentKind Kind)>(StringComparer.Ordinal);
        foreach (var pack in representatives)
        {
            foreach (var item in pack.SeedItems)
            {
                if (!owner.TryGetValue(item.Key, out var existing)
                    || string.CompareOrdinal(pack.PackKey, existing.PackKey) < 0)
                {
                    owner[item.Key] = (pack.PackKey, item.Kind);
                }
            }
        }

        var edges = new List<PackFeatureGraphEdge>();
        foreach (var pack in representatives)
        {
            if (pack.ContentReferences is { Count: > 0 } declared)
            {
                // G2: the CROSS-app (class-3) edges come from the declared, signed references (no body parse
                // — design note §6.3). The INTRA-app (class-1) edges are internal wiring the manifest does
                // NOT carry (cross-app only), so body-parse them still — cheap, and it keeps a G2 pack's
                // graph as complete as a pre-G2 pack's (no silently-dropped intra edges).
                AddDeclaredEdges(edges, pack, declared, owner);
                AddBodyParsedEdges(edges, pack, owner, intraOnly: true);
            }
            else
            {
                // Pre-G2 / dependency-free pack: body-parse everything, exactly as G1 (backward-compatible).
                AddBodyParsedEdges(edges, pack, owner, intraOnly: false);
            }
        }

        edges.Sort(PackFeatureGraphEdgeOrder.Compare);
        return new PackContentEdgeIndex(PackInstallStateFingerprint.Compute(installed), edges);
    }

    /// <summary>
    /// G2 path — maps a pack's declared, signed <see cref="PackContentReferenceEdge"/>s to graph edges. The
    /// declared <c>ToPackKey</c> is authoritative (named even when not currently installed — a dangling but
    /// nameable edge); <c>ToKind</c> is resolved from live install state when the declared target is installed
    /// under the declared app, else the composer-declared kind (or null) stands. The composer emits cross-app
    /// references only, so these are class-3 edges (a defensively same-pack declared edge classifies intra).
    /// </summary>
    private static void AddDeclaredEdges(
        List<PackFeatureGraphEdge> edges,
        InstalledPack pack,
        IReadOnlyList<PackContentReferenceEdge> declared,
        IReadOnlyDictionary<string, (string PackKey, PackContentKind Kind)> owner)
    {
        foreach (var reference in declared)
        {
            PackContentKind? toKind = reference.ToKind;
            if (owner.TryGetValue(reference.ToContentKey, out var target)
                && string.Equals(target.PackKey, reference.ToPackKey, StringComparison.Ordinal))
            {
                toKind = target.Kind; // the target is installed under the declared app — use its real kind.
            }

            var edgeClass = string.Equals(reference.ToPackKey, pack.PackKey, StringComparison.Ordinal)
                ? PackFeatureEdgeClass.IntraApp
                : PackFeatureEdgeClass.CrossAppContentReference;

            edges.Add(new PackFeatureGraphEdge(
                FromPackKey: pack.PackKey,
                FromContentKey: reference.FromContentKey,
                FromKind: reference.FromKind,
                ToPackKey: reference.ToPackKey,
                ToContentKey: reference.ToContentKey,
                ToKind: toKind,
                EdgeClass: edgeClass,
                Relation: reference.Relation));
        }
    }

    /// <summary>
    /// Body-parses a pack's seed items to derive its content-reference edges (the G1 derivation). With
    /// <paramref name="intraOnly"/> false (a pre-G2 pack) it emits BOTH intra + cross-app edges, exactly as
    /// G1; with <paramref name="intraOnly"/> true (a G2 pack whose cross-app edges came from the declared
    /// references) it emits ONLY the intra-app edges, so the pack's cross-app class-3 edge is not
    /// double-counted (declared is authoritative for cross-app) while its internal wiring is still surfaced.
    /// </summary>
    private static void AddBodyParsedEdges(
        List<PackFeatureGraphEdge> edges,
        InstalledPack pack,
        IReadOnlyDictionary<string, (string PackKey, PackContentKind Kind)> owner,
        bool intraOnly)
    {
        foreach (var item in pack.SeedItems)
        {
            JsonNode? body;
            try
            {
                body = JsonNode.Parse(item.CanonicalJson);
            }
            catch (System.Text.Json.JsonException)
            {
                continue; // an unreadable body contributes no edges (tolerant, like the projector's skip).
            }

            foreach (var reference in PackContentReferenceExtractor.Extract(item.Kind, body))
            {
                if (string.Equals(reference.ToContentKey, item.Key, StringComparison.Ordinal))
                {
                    continue; // a self-reference is not a graph edge.
                }

                var hasOwner = owner.TryGetValue(reference.ToContentKey, out var target);
                var toPackKey = hasOwner ? target.PackKey : null;
                PackContentKind? toKind = hasOwner ? target.Kind : null;

                var edgeClass = string.Equals(toPackKey, pack.PackKey, StringComparison.Ordinal)
                    ? PackFeatureEdgeClass.IntraApp
                    : PackFeatureEdgeClass.CrossAppContentReference;

                if (intraOnly && edgeClass != PackFeatureEdgeClass.IntraApp)
                {
                    continue; // a G2 pack's cross-app edges are the DECLARED ones — never the body-parsed ones.
                }

                edges.Add(new PackFeatureGraphEdge(
                    FromPackKey: pack.PackKey,
                    FromContentKey: item.Key,
                    FromKind: item.Kind,
                    ToPackKey: toPackKey,
                    ToContentKey: reference.ToContentKey,
                    ToKind: toKind,
                    EdgeClass: edgeClass,
                    Relation: reference.Relation));
            }
        }
    }
}

/// <summary>
/// The install-state version key for the content-edge-index cache (design note §2.2 "keyed by install-state
/// version"). A stable SHA-256 fingerprint over the representative version of every installed pack key: the
/// key + version + lifecycle + provider slot + declared dependencies + each seed item's key/kind/content
/// address. Any change that would change the derived graph changes the fingerprint, so a stale index is
/// detectable and a rebuild yields the same fingerprint for unchanged state.
/// </summary>
public static class PackInstallStateFingerprint
{
    /// <summary>The fingerprint of the empty install state.</summary>
    public static string Empty { get; } = Compute(Array.Empty<InstalledPack>());

    /// <summary>Computes the deterministic install-state fingerprint over the representative version set.</summary>
    public static string Compute(IReadOnlyList<InstalledPack> installed)
    {
        ArgumentNullException.ThrowIfNull(installed);

        var sb = new StringBuilder();
        foreach (var pack in PackFeatureGraphInstallState.Representatives(installed))
        {
            sb.Append(pack.PackKey).Append('@').Append(pack.Version)
              .Append('#').Append((int)pack.Lifecycle)
              .Append('/').Append(pack.ProviderSlot ?? "-");

            foreach (var dep in pack.Dependencies.Select(d => d.Key).OrderBy(k => k, StringComparer.Ordinal))
            {
                sb.Append(">dep:").Append(dep);
            }

            // The graph now DERIVES class-3 edges from the declared content references (G2), so a change to
            // them must move the fingerprint (else a stale index would keep old edges). Ordinal-sorted for
            // determinism regardless of the stored order.
            foreach (var reference in (pack.ContentReferences ?? Array.Empty<PackContentReferenceEdge>())
                .OrderBy(r => r.FromContentKey, StringComparer.Ordinal)
                .ThenBy(r => r.ToPackKey, StringComparer.Ordinal)
                .ThenBy(r => r.ToContentKey, StringComparer.Ordinal)
                .ThenBy(r => (int)r.Relation))
            {
                sb.Append(">ref:").Append(reference.FromContentKey).Append("->")
                  .Append(reference.ToPackKey).Append('/').Append(reference.ToContentKey)
                  .Append('@').Append((int)reference.Relation);
            }

            foreach (var item in pack.SeedItems.OrderBy(s => s.Key, StringComparer.Ordinal))
            {
                sb.Append('|').Append(item.Key).Append(':').Append((int)item.Kind)
                  .Append(':').Append(item.ContentAddress.ToString());
            }

            sb.Append('\n');
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexStringLower(hash);
    }
}

/// <summary>
/// Shared install-state → representative-version projection used by BOTH the index builder and the read-model
/// (so they cannot disagree on "what an app is"). A pack KEY collapses to a single representative version —
/// its Active version if any, else its highest installed version — mirroring the collision engine's
/// <c>PackCompositionConflicts.ClaimsFromInstalled</c> rule (a same-key clash between two VERSIONS is an
/// upgrade, not a cross-app collision). Output is ordinal-sorted by pack key for determinism.
/// </summary>
internal static class PackFeatureGraphInstallState
{
    public static IReadOnlyList<InstalledPack> Representatives(IReadOnlyList<InstalledPack> installed)
        => installed
            .GroupBy(p => p.PackKey, StringComparer.Ordinal)
            .Select(Representative)
            .OrderBy(p => p.PackKey, StringComparer.Ordinal)
            .ToList();

    private static InstalledPack Representative(IEnumerable<InstalledPack> versions)
    {
        var list = versions as IReadOnlyList<InstalledPack> ?? versions.ToList();
        return list.FirstOrDefault(p => p.Lifecycle == PackLifecycleState.Active)
            ?? list.OrderBy(p => p.Version, Comparer<string>.Create(PackVersion.Compare)).Last();
    }
}

/// <summary>Deterministic total order over feature-graph edges (stable index + graph output).</summary>
internal static class PackFeatureGraphEdgeOrder
{
    public static int Compare(PackFeatureGraphEdge a, PackFeatureGraphEdge b)
    {
        int c = string.CompareOrdinal(a.FromPackKey, b.FromPackKey);
        if (c != 0) return c;
        c = string.CompareOrdinal(a.FromContentKey ?? string.Empty, b.FromContentKey ?? string.Empty);
        if (c != 0) return c;
        c = string.CompareOrdinal(a.ToContentKey ?? string.Empty, b.ToContentKey ?? string.Empty);
        if (c != 0) return c;
        c = string.CompareOrdinal(a.ToPackKey ?? string.Empty, b.ToPackKey ?? string.Empty);
        if (c != 0) return c;
        c = ((int)a.EdgeClass).CompareTo((int)b.EdgeClass);
        if (c != 0) return c;
        return ((int)a.Relation).CompareTo((int)b.Relation);
    }
}
