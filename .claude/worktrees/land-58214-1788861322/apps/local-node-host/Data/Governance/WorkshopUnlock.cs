using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.LocalNodeHost.Data.Governance;

/// <summary>
/// The <c>workshop:unlock</c> PBAC permission — the <b>mode-entry</b> permission that gates re-entering
/// Build in operating phase (ADR 0144 AD.1). It is DISTINCT from the per-pillar <c>&lt;pillar&gt;:design</c>
/// permissions (<c>forms:design</c>, <c>workflows:design</c>, …) you still need to edit once inside: a user
/// entering Build to change a form needs BOTH <c>workshop:unlock</c> (open the mode) AND
/// <c>forms:design</c> (edit the form).
/// </summary>
/// <remarks>
/// <para>
/// <b>The solo founder self-holds it via the seed-graph informal default.</b> Under the seed-grows-into-tree
/// model (ADR 0144 D3) the founder receives the full informal-default role graph at genesis; the node seeds
/// <c>workshop:unlock</c> into the founder's permission set at enrollment
/// (<c>MultiTeamBootstrapHostedService.EnrollOperatorAsync</c>), so a solo operator can always re-enter
/// Build. A growing org MAY later bind <c>workshop:unlock</c> to a second-approver governance template (the
/// SoD/dual-control primitive) — available, never forced (mirrors 0144's "SoD available, never mandatory").
/// </para>
/// <para>
/// <b>Locking down does NOT consume this.</b> setup → operating is the unguarded safe direction; only the
/// re-entry (unlock) is gated on this permission, evaluated by <see cref="INodeWorkshopUnlockAuthority"/>
/// and rendered by the Harborline App's guarded re-entry control (slice B4).
/// </para>
/// </remarks>
public static class WorkshopUnlock
{
    /// <summary>The PBAC permission string — <c>workshop:unlock</c>, sourced from the platform vocabulary
    /// (<see cref="Permission.WorkshopUnlock"/>), which is where its install-wide declaration lives.</summary>
    public const string PermissionKey = Permission.WorkshopUnlock;

    /// <summary>The typed operation the node resolves at its point of use through
    /// <c>AuthorizationGate.DecideAsync</c>. Declared install-wide, so the request carries no record target
    /// (ledger L600/L671).</summary>
    public static AuthorizationOperation Operation { get; } = AuthorizationOperation.Parse(PermissionKey);
}
