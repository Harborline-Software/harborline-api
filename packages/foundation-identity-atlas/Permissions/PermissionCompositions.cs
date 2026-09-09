using System;
using System.Collections.Generic;
using System.Linq;

namespace Harborline.Api.Foundation.IdentityAtlas.Permissions;

/// <summary>
/// The four default COMPOSITIONS (owner / admin / member / support) as accepted in the ONR permission
/// taxonomy (#1284, all defaults accepted by CIC [2026-06-20]). These are mutable named TEMPLATES, not
/// statuses: on admission a member's edge is SEEDED from a template, then the set is independently mutable via
/// <c>grant:permissions</c>. "admin" is whatever its set currently is — no magic (taxonomy §2).
/// </summary>
/// <remarks>
/// <para>
/// <b>admin is NOT god (taxonomy Q5, accepted).</b> The default <c>admin</c> template is a deliberate subset of
/// <c>owner</c>: it runs the org day-to-day but does NOT hold <c>grant:permissions</c>,
/// <c>org:transfer-ownership</c>, or any <c>provider:configure-*</c> — those are independently grantable but
/// OFF by default (most-restrictive-useful). An org that wants a more powerful admin just adds the perm; that
/// IS the PBAC model working as intended.
/// </para>
/// <para>
/// <b>member: data permissions (taxonomy §2).</b> The baseline collaborator holds the live data set.
/// </para>
/// <para>
/// <b>support: the removable tech-support bootstrap bundle (taxonomy §2).</b> Holds the install/setup + grant
/// stack (<c>members:admit</c>, <c>grant:permissions</c>, <c>org:transfer-ownership</c>, every
/// <c>provider:configure-*</c>, <c>org:manage-settings</c>, <c>audit:read</c>) but deliberately NO data perms
/// — support installs, it does not run the books. It may BE the genesis AND be fully revoked from live state
/// afterward (the genesis-vs-live split): support grants the owner the root-grant BEFORE being revoked, so the
/// no-bricking floor is never violated.
/// </para>
/// </remarks>
public static class PermissionCompositions
{
    // ── The data verb families per doctype (taxonomy Family A) ───────────────────────────────────────────

    private static readonly string[] ContactsAll =
        { Permission.ContactsRead, Permission.ContactsCreate, Permission.ContactsWrite, Permission.ContactsArchive };

    // Legacy coarse strings (TeamRolePermissions) emitted ALONGSIDE the PBAC vocabulary so existing
    // HasPermission("ledger:post"/"records:write"/...) consumers keep resolving without a rewrite.
    private static readonly string[] LegacyDataStrings =
        { TeamRolePermissions.RecordsRead, TeamRolePermissions.RecordsWrite };

    /// <summary>The full live data set across enabled doctypes.</summary>
    private static readonly string[] AllData =
        ContactsAll
            // spatial:read rides the DATA sets (admin + member), NOT the Viewer floor — CIC [2026-08-06],
            // card 3777: site coordinates are CP-4 PII-class; the read-only floor never sees them.
            .Append(Permission.SpatialRead)
            .Concat(LegacyDataStrings).Append(TeamRolePermissions.LedgerPost)
            .ToArray();

    // ── owner — the grant-substrate holder (genesis default): everything ─────────────────────────────────

    /// <summary>
    /// owner = ALL_DATA ∪ full Family B (admit/revoke/grant/transfer) ∪ cross-cutting settings. The bootstrap /
    /// genesis composition. By the no-bricking floor ≥1 member must always hold {grant:permissions,
    /// org:transfer-ownership}; owner can only self-revoke after transferring ownership.
    /// </summary>
    public static PermissionSet Owner { get; } = PermissionSet.From(
        AllData
            .Append(Permission.MembersAdmit).Append(Permission.MembersRevoke)
            .Append(Permission.GrantPermissions).Append(Permission.OrgTransferOwnership)
            .Append(Permission.AuditRead).Append(Permission.OrgManageSettings)
            // Ticket 212: the member authorization seed offers own-decision trace reads. The genesis
            // admitter must hold that atom too, so pairing can transcribe it under the no-escalation guard.
            .Append(Permission.AuditTraceRead)
            .Append(Permission.OrgBrandingWrite)
            .Append(Permission.PackagesAuthor).Append(Permission.PackagesOperate)
            .Append(Permission.SchedulingRead).Append(Permission.SchedulingAuthor).Append(Permission.SchedulingOperate)
            .Append(Permission.FormsAuthor)
            .Append(TeamRolePermissions.MembersManage));

    // ── admin — composable governance subset (explicitly NOT god; taxonomy Q5) ───────────────────────────

    /// <summary>
    /// admin = ALL_DATA ∪ {members:admit, members:revoke} [governance EXCEPT grant/transfer] ∪ {audit:read,
    /// org:manage-settings} ∪ {packages:author, packages:operate}. Per CIC Q5 (accepted): admin does NOT
    /// hold grant:permissions or org:transfer-ownership by default.
    /// <para><b>Packaging (ADR 0145 / council A-1).</b> admin holds BOTH packaging permissions because the
    /// single-office node enrolls its operator as admin, so this IS the "solo founder holds both via the
    /// informal default" posture the dogfood cut needs. At multi-principal / 0144 org-model fan-out,
    /// packages:author tightens to the authoring role (a grant:permissions change, since these are mutable
    /// templates) — the council-named follow-up.</para>
    /// </summary>
    public static PermissionSet Admin { get; } = PermissionSet.From(
        AllData
            .Append(Permission.MembersAdmit).Append(Permission.MembersRevoke)
            .Append(Permission.AuditRead).Append(Permission.OrgManageSettings)
            .Append(Permission.OrgBrandingWrite)
            .Append(Permission.PackagesAuthor).Append(Permission.PackagesOperate)
            .Append(Permission.SchedulingRead).Append(Permission.SchedulingAuthor).Append(Permission.SchedulingOperate)
            .Append(Permission.FormsAuthor)
            .Append(TeamRolePermissions.MembersManage));

    // ── member — everyday doctype permissions ────────────────────────────────────────────────────────────

    /// <summary>
    /// member = all data permissions. No governance or cross-cutting permissions. The baseline collaborator.
    /// </summary>
    public static PermissionSet Member { get; } = PermissionSet.From(
        AllData.Append(Permission.SchedulingRead));

    // ── support — the tech-support bootstrap bundle (removable; NO data perms) ────────────────────────────

    /// <summary>
    /// support = {members:admit, grant:permissions, org:transfer-ownership} [can bootstrap + hand off] ∪
    /// {org:manage-settings, audit:read}.
    /// Deliberately NO data perms — support installs, it does not run the books. Holding BOTH grant:permissions
    /// AND org:transfer-ownership is what lets the handoff satisfy the no-bricking floor at every step.
    /// </summary>
    public static PermissionSet Support { get; } = PermissionSet.From(
        new[] { Permission.MembersAdmit, Permission.GrantPermissions, Permission.OrgTransferOwnership }
            .Append(Permission.OrgManageSettings).Append(Permission.AuditRead)
            // Support INSTALLS packs (part of setup) but does not AUTHOR them — operate only.
            .Append(Permission.PackagesOperate));

    /// <summary>
    /// Map the legacy structured <see cref="TeamRole"/> enum to its default PBAC composition, so the
    /// <c>RoleDisplayName</c>→<c>PermissionSet</c> migration can seed an edge from either a role label or a
    /// template. <see cref="TeamRole.Admin"/> seeds <see cref="Admin"/> (NOT owner — the single-office operator
    /// runs the books; ownership transfer is a separate explicit step); <see cref="TeamRole.Member"/> seeds
    /// <see cref="Member"/>; <see cref="TeamRole.Viewer"/> seeds the read-only floor.
    /// </summary>
    public static PermissionSet ForRole(TeamRole role) => role switch
    {
        TeamRole.Admin => Admin,
        TeamRole.Member => Member,
        TeamRole.Viewer => Viewer,
        _ => PermissionSet.Empty,
    };

    /// <summary>Maps the retired ShipRole vocabulary to the live PBAC compositions.</summary>
    public static PermissionSet ForLegacyShipRoleName(string role) => role switch
    {
        "Captain" => Owner,
        "XO" => Admin,
        "Scribe" => Member,
        // These legacy duty stations have no one-to-one PBAC composition. Keep their compatibility
        // projection explicitly empty rather than silently widening them to Member.
        "EngineerOfficer" or "Navigator" or "TacticalOfficer" or "DivisionOfficer" or
        "IDC" or "SUPPO" or "OOD" or "EOOW" => PermissionSet.Empty,
        _ => throw new ArgumentOutOfRangeException(
            nameof(role), role, "Unknown legacy ShipRole; add its PBAC composition explicitly."),
    };

    /// <summary>Unions the PBAC compatibility bundle for each legacy ShipRole name.</summary>
    public static PermissionSet ForLegacyShipRoleNames(IEnumerable<string> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);
        var result = PermissionSet.Empty;
        foreach (var role in roles)
        {
            result = result.Union(ForLegacyShipRoleName(role));
        }

        return result;
    }

    /// <summary>The read-only floor — live doctype read verbs (the <see cref="TeamRole.Viewer"/>
    /// seed). Distinct from <see cref="Member"/> (no create/write/archive/append). DELIBERATE OMISSION:
    /// <see cref="Permission.SpatialRead"/> is NOT here — site coordinates are CP-4 PII-class and the CIC
    /// ruled [2026-08-06] (card 3777) that the viewer floor never sees them; spatial reads are an
    /// admin/member data permission.</summary>
    public static PermissionSet Viewer { get; } = PermissionSet.Of(
        Permission.ContactsRead, TeamRolePermissions.RecordsRead);

    /// <summary>
    /// Resolve a named composition by its conventional name ("owner"/"admin"/"member"/"support"/"viewer"),
    /// case-insensitively. Returns <c>null</c> for an unknown name (the caller decides — typically seed
    /// <see cref="PermissionSet.Empty"/> or reject).
    /// </summary>
    public static PermissionSet? ByName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.Trim().ToLowerInvariant() switch
        {
            "owner" => Owner,
            "admin" => Admin,
            "member" => Member,
            "support" => Support,
            "viewer" => Viewer,
            _ => null,
        };
    }
}
