using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Model;

namespace Harborline.Api.Foundation.Packs.Graph;

/// <summary>
/// The provenance class of a feature-graph edge (app-layer design note §2.1). The three classes differ in
/// WHERE the edge is derived from, which matters for trust + cost:
/// </summary>
public enum PackFeatureEdgeClass
{
    /// <summary>Intra-app: a reference between two contributables of the SAME app (a form binds its own
    /// type). Body-derived by the content-edge-index.</summary>
    IntraApp = 1,

    /// <summary>Cross-app dependency: app B declares a pack-level <c>Dependencies[]</c> edge on app A
    /// (ADR 0129 D3 — <c>composes-over</c>). Read straight off <see cref="InstalledPack.Dependencies"/>;
    /// no body parse.</summary>
    CrossAppDependency = 2,

    /// <summary>Cross-app content reference: app B's contributable references a contributable of a DIFFERENT
    /// app (app B's type sets <c>parentType</c> to app A's type). Body-derived by the content-edge-index. In
    /// G1 this is derived by parsing bodies; G2 replaces the parse with a signed manifest
    /// <c>ContentReferences[]</c> edge.</summary>
    CrossAppContentReference = 3,
}

/// <summary>
/// The kind of reference an edge expresses (app-layer design note §6.3 relation vocabulary). Mirrors the
/// proposed G2 <c>ContentReferences[].relation</c> values so the derived-today edge and the declared-in-G2
/// edge speak one vocabulary.
/// </summary>
public enum PackFeatureEdgeRelation
{
    /// <summary>A form (or other consumer) binds a record type.</summary>
    BindsTo = 0,

    /// <summary>A record type's <c>parentType</c> points at another type (type hierarchy).</summary>
    ParentOf = 1,

    /// <summary>A help item anchors a surface / another contributable.</summary>
    Anchors = 2,

    /// <summary>A generic reference (a workflow references a form, etc.).</summary>
    References = 3,

    /// <summary>A pack composes over another pack (the pack-level <c>Dependencies[]</c> edge).</summary>
    ComposesOver = 4,
}

/// <summary>
/// One contributable node in the feature graph — a single manifest <c>Contents[]</c> leaf, projected to the
/// customer view: its stable content key, its dev kind, the customer <see cref="Pillar"/> it groups under,
/// the domain-generic <see cref="KindNoun"/> token, and the concrete app-supplied <see cref="Label"/>
/// (a type's <c>displayName</c> — "Vehicle"; <c>null</c> when the body carries none). <see cref="OwningPackKey"/>
/// is the app that added it — provenance derived from install state (the single source of truth), never a
/// second store.
/// </summary>
/// <param name="ContentKey">The stable content key (app-namespaced — "fleet-ops.vehicle").</param>
/// <param name="Kind">The declarative dev kind (never surfaced raw to a customer).</param>
/// <param name="Pillar">The customer grouping bucket.</param>
/// <param name="KindNoun">The domain-generic customer-noun token for the kind ("recordType", …).</param>
/// <param name="Label">The concrete app-supplied display label (the body's <c>displayName</c>), or null.</param>
/// <param name="Version">The pinned item version.</param>
/// <param name="OwningPackKey">The app (pack key) that added this contributable (provenance).</param>
public sealed record PackFeatureGraphNode(
    string ContentKey,
    PackContentKind Kind,
    PackPillar Pillar,
    string KindNoun,
    string? Label,
    string Version,
    string OwningPackKey);

/// <summary>
/// One edge in the feature graph — a reference between two contributables (or, for
/// <see cref="PackFeatureEdgeClass.CrossAppDependency"/>, between two apps). Deterministic + directional
/// (from → to). For a cross-app content reference whose target key is not resolvable to an installed pack,
/// <see cref="ToPackKey"/> is <c>null</c> (a dangling reference — the "requires X — not installed" seam the
/// G4 preview renders fail-closed).
/// </summary>
/// <param name="FromPackKey">The app the edge originates from.</param>
/// <param name="FromContentKey">The originating contributable's key (null for a pack-level dependency edge).</param>
/// <param name="FromKind">The originating contributable's kind (null for a pack-level dependency edge).</param>
/// <param name="ToPackKey">The target app (null when a cross-app reference does not resolve to an installed app).</param>
/// <param name="ToContentKey">The target contributable's key (null for a pack-level dependency edge).</param>
/// <param name="ToKind">The target contributable's kind when known, else null.</param>
/// <param name="EdgeClass">The provenance class of the edge.</param>
/// <param name="Relation">The reference relation.</param>
public sealed record PackFeatureGraphEdge(
    string FromPackKey,
    string? FromContentKey,
    PackContentKind? FromKind,
    string? ToPackKey,
    string? ToContentKey,
    PackContentKind? ToKind,
    PackFeatureEdgeClass EdgeClass,
    PackFeatureEdgeRelation Relation);

/// <summary>An app's contributions of one pillar — the "Records (6): Vehicle · Trailer · …" row of the card.</summary>
/// <param name="Pillar">The pillar bucket.</param>
/// <param name="Nodes">The contributables under it (stable order — by content key, ordinal).</param>
public sealed record PackPillarGroup(PackPillar Pillar, IReadOnlyList<PackFeatureGraphNode> Nodes);

/// <summary>
/// One app's slice of the feature graph — the per-app card's data (design note §3.1). The app is a DISTINCT
/// installed pack key at its representative version (Active if any, else the highest installed version — the
/// same collapse the collision engine uses, so "an app" means one thing across both surfaces).
/// </summary>
/// <param name="PackKey">The app (pack key).</param>
/// <param name="Version">The representative version rendered.</param>
/// <param name="ScopeTier">The pack scope-tier.</param>
/// <param name="Lifecycle">Draft / Active / Superseded / Inactive — the card's state badge.</param>
/// <param name="Pillars">The contributions grouped by pillar (stable pillar order).</param>
/// <param name="DependsOn">The app's OUTGOING cross-app edges (class 2 pack dependency + class 3 content
/// references) — the "Depends on" line.</param>
/// <param name="ProviderSlot">The exclusive category slot this app provides, or null.</param>
/// <param name="ContributionCount">The total number of contributables (the "N contributions" count).</param>
public sealed record PackAppGraphSlice(
    string PackKey,
    string Version,
    PackScopeTier ScopeTier,
    PackLifecycleState Lifecycle,
    IReadOnlyList<PackPillarGroup> Pillars,
    IReadOnlyList<PackFeatureGraphEdge> DependsOn,
    string? ProviderSlot,
    int ContributionCount);

/// <summary>
/// The whole feature graph for a tenant — every installed app's slice plus every edge (intra + cross-app).
/// DERIVED at read time from install state; <see cref="InstallStateFingerprint"/> is the install-state
/// version the derivation ran against (the content-edge-index cache key), so two reads over unchanged state
/// return the same fingerprint and a change is detectable. The customer never sees "the graph" — surfaces
/// render slices of it (Apps cards, install diff, provenance chip).
/// </summary>
/// <param name="InstallStateFingerprint">The install-state version this derivation reflects.</param>
/// <param name="Apps">One slice per installed app (stable order — by pack key, ordinal).</param>
/// <param name="Edges">Every edge in the graph (stable order).</param>
public sealed record PackFeatureGraph(
    string InstallStateFingerprint,
    IReadOnlyList<PackAppGraphSlice> Apps,
    IReadOnlyList<PackFeatureGraphEdge> Edges);
