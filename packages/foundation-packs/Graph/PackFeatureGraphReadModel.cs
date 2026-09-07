using System.Text.Json.Nodes;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Model;

namespace Harborline.Api.Foundation.Packs.Graph;

/// <summary>
/// The feature-graph read-model (app-layer design note G1 keystone) — assembles the per-app contribution
/// graph at READ time from install state (the single source of truth) plus the rebuildable
/// content-edge-index. There is NO independent graph store: an "Apps" card and an install preview call this
/// same read-model, so they can never disagree (design note §2.2). Provenance ("which app added this") is
/// derived from install membership — an item's owning app is the pack whose seed layer carries it — never a
/// second ledger that could drift (§1 "the install/projection layer is the single source of truth").
/// </summary>
public interface IPackFeatureGraphReadModel
{
    /// <summary>The whole feature graph for a tenant — one slice per installed app + every edge.</summary>
    PackFeatureGraph GetGraph(TenantId tenant);

    /// <summary>One app's slice ("what this app added"), or null if the app is not installed.</summary>
    PackAppGraphSlice? GetApp(TenantId tenant, string packKey);
}

/// <inheritdoc />
public sealed class PackFeatureGraphReadModel : IPackFeatureGraphReadModel
{
    private readonly IPackInstallStore _store;
    private readonly IPackContentEdgeIndexProvider _edgeIndex;

    /// <summary>Constructs the read-model over the install store + the content-edge-index provider.</summary>
    public PackFeatureGraphReadModel(IPackInstallStore store, IPackContentEdgeIndexProvider edgeIndex)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _edgeIndex = edgeIndex ?? throw new ArgumentNullException(nameof(edgeIndex));
    }

    /// <inheritdoc />
    public PackFeatureGraph GetGraph(TenantId tenant)
    {
        var representatives = PackFeatureGraphInstallState.Representatives(_store.ListInstalled(tenant));
        var index = _edgeIndex.GetOrRebuild(tenant);

        // Class-2 pack-level dependency edges are read straight off install state (no body parse); the
        // class-1/3 body-derived edges come from the index. The union is the whole graph.
        var edges = new List<PackFeatureGraphEdge>(index.ContentEdges);
        foreach (var pack in representatives)
        {
            foreach (var dep in pack.Dependencies)
            {
                edges.Add(new PackFeatureGraphEdge(
                    FromPackKey: pack.PackKey,
                    FromContentKey: null,
                    FromKind: null,
                    ToPackKey: dep.Key,
                    ToContentKey: null,
                    ToKind: null,
                    EdgeClass: PackFeatureEdgeClass.CrossAppDependency,
                    Relation: PackFeatureEdgeRelation.ComposesOver));
            }
        }

        edges.Sort(PackFeatureGraphEdgeOrder.Compare);

        var slices = representatives.Select(pack => BuildSlice(pack, edges)).ToList();
        return new PackFeatureGraph(index.InstallStateFingerprint, slices, edges);
    }

    /// <inheritdoc />
    public PackAppGraphSlice? GetApp(TenantId tenant, string packKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packKey);
        return GetGraph(tenant).Apps
            .FirstOrDefault(a => string.Equals(a.PackKey, packKey, StringComparison.Ordinal));
    }

    private static PackAppGraphSlice BuildSlice(InstalledPack pack, IReadOnlyList<PackFeatureGraphEdge> allEdges)
    {
        var nodes = pack.SeedItems
            .Select(item => new PackFeatureGraphNode(
                ContentKey: item.Key,
                Kind: item.Kind,
                Pillar: PackPillarMap.ForKind(item.Kind),
                KindNoun: PackPillarMap.GenericNoun(item.Kind),
                Label: ReadLabel(item),
                Version: item.Version,
                OwningPackKey: pack.PackKey))
            .ToList();

        var pillars = nodes
            .GroupBy(n => n.Pillar)
            .OrderBy(g => (int)g.Key)
            .Select(g => new PackPillarGroup(
                g.Key,
                g.OrderBy(n => n.ContentKey, StringComparer.Ordinal).ToList()))
            .ToList();

        // An app's "Depends on" line = its OUTGOING cross-app edges (a pack-level dependency or a
        // content reference into another app). Intra-app edges are internal wiring, not a dependency.
        var dependsOn = allEdges
            .Where(e => string.Equals(e.FromPackKey, pack.PackKey, StringComparison.Ordinal)
                && e.EdgeClass != PackFeatureEdgeClass.IntraApp)
            .ToList();

        return new PackAppGraphSlice(
            PackKey: pack.PackKey,
            Version: pack.Version,
            ScopeTier: pack.ScopeTier,
            Lifecycle: pack.Lifecycle,
            Pillars: pillars,
            DependsOn: dependsOn,
            ProviderSlot: pack.ProviderSlot,
            ContributionCount: nodes.Count);
    }

    /// <summary>
    /// Reads a contributable's concrete display label from its body (<c>displayName</c>) — app-supplied DATA
    /// (e.g. "Vehicle"), not platform copy, so it is exempt from the domain-generic rule. Tolerant: a body
    /// with no readable label yields null and the Harborline App falls back to the generic kind noun.
    /// </summary>
    private static string? ReadLabel(PackSeedItem item)
    {
        try
        {
            if (JsonNode.Parse(item.CanonicalJson) is JsonObject obj
                && obj.TryGetPropertyValue("displayName", out var node)
                && node is JsonValue v
                && v.TryGetValue<string>(out var label)
                && !string.IsNullOrWhiteSpace(label))
            {
                return label.Trim();
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Unreadable body → no label (the Harborline App uses the generic kind noun). Never throws.
        }

        return null;
    }
}
