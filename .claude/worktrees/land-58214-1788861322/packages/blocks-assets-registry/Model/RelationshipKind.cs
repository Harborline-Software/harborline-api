namespace Harborline.Api.Blocks.Assets.Registry.Model;

/// <summary>
/// The kind of a <see cref="TypedRelationship"/> (annex §3.1). Relationships are
/// first-class, typed, and dated — hierarchy is a VIEW over these edges, never a stored
/// parent pointer (ADR 0101 Rev 3.1 supersedes the annex §3.4 <c>ParentAssetId</c> wording).
/// </summary>
public enum RelationshipKind
{
    /// <summary>
    /// Spatial containment (container → child). The one kind subject to a cycle-guard and a
    /// depth bound: a containment cycle is impossible to persist (A5b), so tree browse, subtree
    /// campaign scoping, and move-in/out subtree snapshots can never brick.
    /// </summary>
    Contains = 0,

    /// <summary>
    /// A <b>movable</b> entity's location over time (entity → container/place). Dated
    /// (<see cref="TypedRelationship.EffectiveFrom"/> / <see cref="TypedRelationship.EffectiveTo"/>)
    /// so the edge history IS the move history.
    /// </summary>
    LocatedAt = 1,

    /// <summary>
    /// Cross-cutting system membership (entity → system). <b>Multi-membership allowed</b> — a
    /// component may serve two systems (shared plant, condensate pump) — which is exactly the
    /// second hand-maintained tree this model avoids. Not containment: never cycle/depth checked.
    /// </summary>
    PartOfSystem = 2,
}
