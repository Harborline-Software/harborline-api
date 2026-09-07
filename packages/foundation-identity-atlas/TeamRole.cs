namespace Harborline.Api.Foundation.IdentityAtlas;

/// <summary>
/// Structured per-(actor, team) role on the membership edge — the enforcement-grade
/// companion to <see cref="TeamMembership.RoleDisplayName"/> (the localized display string).
/// </summary>
/// <remarks>
/// <para>
/// ADR 0032 / ADR 0066 §Phase 3 put the role <em>on the membership edge</em> so a person can
/// be <see cref="Admin"/> in one org and <see cref="Viewer"/> in another — the role is a
/// property of the (actor, org) pair, not of the actor. Per the multi-org identity survey
/// (#1275 §3), v1 widens the display-only role to a small fixed enum so the existing
/// <c>IAuthorizationContext.HasPermission</c> contract can resolve a real per-org role
/// (CIC [2026-06-20] "role-based per-doctype IN v1").
/// </para>
/// <para>
/// The role → permission-string projection lives in <see cref="TeamRolePermissions"/> so the
/// node's authorization context can answer <c>HasPermission</c> per the active org's role
/// without the financial cluster taking a dependency on this enum.
/// </para>
/// </remarks>
public enum TeamRole
{
    /// <summary>
    /// Full administrative control of the org's books — posts to the GL, manages members,
    /// closes periods. The single-office default operator is an <see cref="Admin"/>.
    /// </summary>
    Admin = 0,

    /// <summary>
    /// A participating member — can read and create operational records (contacts, invoices,
    /// bills) but is not granted GL-administration / member-management authority.
    /// </summary>
    Member = 1,

    /// <summary>Read-only participation in the org — no write authority.</summary>
    Viewer = 2,
}
