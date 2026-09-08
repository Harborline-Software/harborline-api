using System;
using System.Collections.Generic;

namespace Harborline.Api.Foundation.IdentityAtlas;

/// <summary>
/// Projects a structured <see cref="TeamRole"/> to the permission strings the existing
/// <c>IAuthorizationContext.HasPermission(string)</c> contract consumes (ADR 0091).
/// </summary>
/// <remarks>
/// <para>
/// Per the multi-org identity survey (#1275 §3), per-org roles are resolved from the
/// membership edge (<see cref="TeamMembership.Role"/>) for the <em>active</em> org. This
/// type is the role → permission map so the node's authorization context can answer
/// <c>HasPermission</c> against the active org's role without the financial cluster taking a
/// dependency on the role enum — it only ever sees permission strings.
/// </para>
/// <para>
/// The permission vocabulary is deliberately small (v1, CIC [2026-06-20] "role-based per-doctype"):
/// <list type="bullet">
///   <item><see cref="LedgerPost"/> — post to the general ledger / close periods (Admin only).</item>
///   <item><see cref="MembersManage"/> — admit / revoke / set-role for org members (Admin only).</item>
///   <item><see cref="RecordsWrite"/> — create + edit operational records (Admin, Member).</item>
///   <item><see cref="RecordsRead"/> — read records (everyone, incl. Viewer).</item>
/// </list>
/// Richer permission sets are an additive change to this map; the resolution path does not change.
/// </para>
/// </remarks>
public static class TeamRolePermissions
{
    /// <summary>Post to the general ledger and manage fiscal periods. Admin only.</summary>
    public const string LedgerPost = "ledger:post";

    /// <summary>Admit, revoke, or set-role for org members. Admin only.</summary>
    public const string MembersManage = "members:manage";

    /// <summary>Create and edit operational records (contacts, invoices, bills, etc.).</summary>
    public const string RecordsWrite = "records:write";

    /// <summary>Read operational records.</summary>
    public const string RecordsRead = "records:read";

    private static readonly IReadOnlySet<string> AdminPermissions =
        new HashSet<string>(StringComparer.Ordinal) { LedgerPost, MembersManage, RecordsWrite, RecordsRead };

    private static readonly IReadOnlySet<string> MemberPermissions =
        new HashSet<string>(StringComparer.Ordinal) { RecordsWrite, RecordsRead };

    private static readonly IReadOnlySet<string> ViewerPermissions =
        new HashSet<string>(StringComparer.Ordinal) { RecordsRead };

    private static readonly IReadOnlySet<string> NoPermissions =
        new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The set of permission strings granted by <paramref name="role"/>.</summary>
    public static IReadOnlySet<string> ForRole(TeamRole role) => role switch
    {
        TeamRole.Admin => AdminPermissions,
        TeamRole.Member => MemberPermissions,
        TeamRole.Viewer => ViewerPermissions,
        _ => NoPermissions,
    };

    /// <summary>True iff <paramref name="role"/> grants <paramref name="permission"/>.</summary>
    public static bool Grants(TeamRole role, string permission)
    {
        ArgumentNullException.ThrowIfNull(permission);
        return ForRole(role).Contains(permission);
    }

    /// <summary>The role names (display strings) for each <see cref="TeamRole"/>.</summary>
    public static string DisplayName(TeamRole role) => role switch
    {
        TeamRole.Admin => "Admin",
        TeamRole.Member => "Member",
        TeamRole.Viewer => "Viewer",
        _ => role.ToString(),
    };
}
