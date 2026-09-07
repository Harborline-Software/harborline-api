using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Harborline.Api.Foundation.Packs.Graph;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Financial;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// The app-layer FEATURE GRAPH read route (G1 keystone; design note
/// <c>_shared/design/app-layer-feature-graph-2026-07-07.md</c>). <c>GET /packs/graph</c> is tenant-scoped,
/// READ-ONLY, and non-mutating — the same posture as the sibling <c>GET /packs/installed</c> route — and
/// returns the derived feature graph: every installed app's contributions grouped by pillar, with per-item
/// provenance and the cross-app edges. It is a projection over install state (no new store, no manifest
/// change — that is G2); the Apps surface (G3) and the install-diff preview (G4) render slices of what this
/// route returns. <c>?app=&lt;key&gt;</c> returns a single app's slice (404 if not installed).
/// </summary>
public static class PackGraphRoutes
{
    /// <summary>Route: the tenant's feature graph (optionally clipped to one app via <c>?app=</c>).</summary>
    public const string GraphRoute = "/api/local-node/packs/graph";

    /// <summary>Maps the read-only graph route, closing over the host-resolved read-model + team accessor.</summary>
    public static void Map(
        IEndpointRouteBuilder app,
        IPackFeatureGraphReadModel readModel,
        IActiveTeamAccessor activeTeam)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(readModel);
        ArgumentNullException.ThrowIfNull(activeTeam);

        app.MapGet(GraphRoute, (HttpRequest request) =>
        {
            var tenant = NodeTenant.Resolve(activeTeam);
            var appKey = request.Query["app"].ToString();

            if (!string.IsNullOrWhiteSpace(appKey))
            {
                var slice = readModel.GetApp(tenant, appKey);
                return slice is null
                    ? Results.NotFound(new { error = $"app '{appKey}' is not installed." })
                    : Results.Ok(ToSliceDto(slice));
            }

            var graph = readModel.GetGraph(tenant);
            return Results.Ok(new PackGraphDto(
                InstallStateFingerprint: graph.InstallStateFingerprint,
                Apps: graph.Apps.Select(ToSliceDto).ToList(),
                Edges: graph.Edges.Select(ToEdgeDto).ToList()));
        });
    }

    private static PackAppSliceDto ToSliceDto(PackAppGraphSlice slice) => new(
        PackKey: slice.PackKey,
        Version: slice.Version,
        ScopeTier: slice.ScopeTier.ToString(),
        Lifecycle: slice.Lifecycle.ToString(),
        ContributionCount: slice.ContributionCount,
        ProviderSlot: slice.ProviderSlot,
        Pillars: slice.Pillars.Select(p => new PackPillarGroupDto(
            Pillar: p.Pillar.ToString(),
            Nodes: p.Nodes.Select(ToNodeDto).ToList())).ToList(),
        DependsOn: slice.DependsOn.Select(ToEdgeDto).ToList());

    private static PackGraphNodeDto ToNodeDto(PackFeatureGraphNode node) => new(
        ContentKey: node.ContentKey,
        Kind: node.Kind.ToString(),
        Pillar: node.Pillar.ToString(),
        KindNoun: node.KindNoun,
        Label: node.Label,
        Version: node.Version,
        OwningPackKey: node.OwningPackKey);

    private static PackGraphEdgeDto ToEdgeDto(PackFeatureGraphEdge edge) => new(
        FromPackKey: edge.FromPackKey,
        FromContentKey: edge.FromContentKey,
        FromKind: edge.FromKind?.ToString(),
        ToPackKey: edge.ToPackKey,
        ToContentKey: edge.ToContentKey,
        ToKind: edge.ToKind?.ToString(),
        EdgeClass: edge.EdgeClass.ToString(),
        Relation: edge.Relation.ToString());
}

// ── Wire DTOs (local to the routes; enums as STABLE tokens the Harborline App localizes) ─────────────

/// <summary>The whole feature graph for the tenant.</summary>
public sealed record PackGraphDto(
    string InstallStateFingerprint,
    IReadOnlyList<PackAppSliceDto> Apps,
    IReadOnlyList<PackGraphEdgeDto> Edges);

/// <summary>One app's slice — the per-app card data.</summary>
public sealed record PackAppSliceDto(
    string PackKey,
    string Version,
    string ScopeTier,
    string Lifecycle,
    int ContributionCount,
    string? ProviderSlot,
    IReadOnlyList<PackPillarGroupDto> Pillars,
    IReadOnlyList<PackGraphEdgeDto> DependsOn);

/// <summary>An app's contributions of one pillar.</summary>
public sealed record PackPillarGroupDto(string Pillar, IReadOnlyList<PackGraphNodeDto> Nodes);

/// <summary>One contributable node (a manifest Contents[] leaf, projected to the customer view).</summary>
public sealed record PackGraphNodeDto(
    string ContentKey,
    string Kind,
    string Pillar,
    string KindNoun,
    string? Label,
    string Version,
    string OwningPackKey);

/// <summary>One edge between contributables (or, for a dependency edge, between apps).</summary>
public sealed record PackGraphEdgeDto(
    string FromPackKey,
    string? FromContentKey,
    string? FromKind,
    string? ToPackKey,
    string? ToContentKey,
    string? ToKind,
    string EdgeClass,
    string Relation);
