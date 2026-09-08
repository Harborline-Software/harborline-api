using System;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.Foundation.IdentityAtlas;

/// <summary>
/// Team membership record returned by <see cref="ITeamRegistry"/> per ADR 0066 §Phase 3.
/// <see cref="TeamId"/> is a <see cref="Guid"/> per the cycle-break decision documented in
/// <c>ActiveTeamOverviewViewModel</c> in <c>Harborline.Api.UICore.Wayfinder</c> —
/// <c>kernel-runtime</c> already references <c>ui-core</c>, so foundation packages
/// MUST NOT reference <c>kernel-runtime.Teams.TeamId</c> to avoid a circular dependency.
/// Consumers in kernel-runtime / accelerators wrap the Guid back into
/// <c>Harborline.Api.Kernel.Runtime.Teams.TeamId</c> at the boundary.
/// </summary>
/// <remarks>
/// <para>
/// <b>PBAC migration (enrollment Phase A; taxonomy #1284 Q6 accepted).</b> The membership edge now carries a
/// MUTABLE <see cref="Permissions"/> set — the authorization TRUTH (CIC [2026-06-20] "admin is a composition
/// of permissions that can be revoked or added to"). <see cref="RoleDisplayName"/> is retained as a cosmetic
/// display label and <see cref="Role"/> as the back-compat structured enum; both are derived seeds for the
/// permission set, not the source of truth once an edge's set is independently mutated via
/// <c>grant:permissions</c>. The authorization context's <c>HasPermission</c> resolves against
/// <see cref="Permissions"/>. When a producer supplies only a role (legacy path), <see cref="Permissions"/>
/// defaults to that role's composition (<see cref="PermissionCompositions.ForRole"/>) so older callers still
/// construct a valid, fully-resolvable edge.
/// </para>
/// <para>
/// <b>Trust-roster fields (enrollment Phase A).</b> <see cref="MemberPublicKey"/> binds this party to a
/// signing key (the party→pubkey map forge-proof attribution needs), and <see cref="AdmissionSignature"/>
/// roots the edge in the genesis-anchored signed membership log (each admission signed by an in-roster admin
/// who holds <c>members:admit</c>; genesis = the founder self-admits). Both are nullable so a legacy/display
/// membership constructed without enrollment data is still valid; a roster used for the trust gate /
/// forge-proof attribution requires them present + validated (see <c>MemberRoster</c>).
/// </para>
/// </remarks>
/// <param name="TeamId">Team identifier as a raw Guid (cycle-break).</param>
/// <param name="DisplayName">Human-readable team display name.</param>
/// <param name="RoleDisplayName">
/// Cosmetic display label for the actor's role within this team (localized). NOT the authorization truth —
/// <see cref="Permissions"/> is.
/// </param>
/// <param name="SubkeyFingerprint">Fingerprint of the actor's team-scoped sub-key.</param>
/// <param name="Role">
/// Back-compat structured per-(actor, team) role (multi-org identity survey #1275 §3). Defaults to
/// <see cref="TeamRole.Member"/>. Seeds <see cref="Permissions"/> when no explicit set is supplied.
/// </param>
/// <param name="Permissions">
/// The MUTABLE PBAC permission set this edge holds — the authorization truth (taxonomy Q6). When null
/// (legacy producers), it resolves to <see cref="PermissionCompositions.ForRole"/> over <see cref="Role"/>.
/// Use <see cref="EffectivePermissions"/> to read the resolved set.
/// </param>
/// <param name="MemberPublicKey">
/// base64url of the member's Ed25519 public key — the party→pubkey binding the trust roster validates and
/// forge-proof attribution checks. Null for a display-only / pre-enrollment edge.
/// </param>
/// <param name="AdmissionSignature">
/// The admission record signing this edge into the genesis-rooted membership log. Null for a display-only /
/// pre-enrollment edge.
/// </param>
public sealed record TeamMembership(
    Guid TeamId,
    string DisplayName,
    string RoleDisplayName,
    KeyFingerprint SubkeyFingerprint,
    TeamRole Role = TeamRole.Member,
    PermissionSet? Permissions = null,
    string? MemberPublicKey = null,
    AdmissionSignature? AdmissionSignature = null)
{
    /// <summary>
    /// The resolved effective permission set: the explicit <see cref="Permissions"/> when present, else the
    /// default composition for <see cref="Role"/> (the legacy seed path). Always non-null — this is what
    /// authorization resolves <c>HasPermission</c> against.
    /// </summary>
    public PermissionSet EffectivePermissions => Permissions ?? PermissionCompositions.ForRole(Role);
}
