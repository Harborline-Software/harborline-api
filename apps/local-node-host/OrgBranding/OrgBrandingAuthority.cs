using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.LocalNodeHost.OrgBranding;

/// <summary>
/// The write-authority rule for org branding (tenant-branding design §3.3, slice T1). Changing branding
/// alters what EVERYONE in the org sees (chrome) and what customers receive (documents), so it is
/// owner/admin-scoped: gated on the <see cref="Permission.OrgBrandingWrite"/> permission string in the
/// ADR 0144 D1 bundle model. The solo founder is seeded the <see cref="PermissionCompositions.Owner"/>
/// composition, which holds it — so the founder resolves as the branding owner by construction.
/// </summary>
/// <remarks>
/// This is a pure resolution helper. Route-boundary ENFORCEMENT of node-loopback PBAC is a separate,
/// platform-wide follow-up (the multi-user-enrollment precondition); until it lands, the write route records
/// the server-derived <c>updatedBy</c> for audit and this helper is the authoritative branding-owner rule the
/// settings surface (T3) consults to render the section read-only for a non-holder.
/// </remarks>
public static class OrgBrandingAuthority
{
    /// <summary>True iff the given permission set may change org branding (holds
    /// <see cref="Permission.OrgBrandingWrite"/>).</summary>
    public static bool CanWrite(PermissionSet permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        return permissions.Contains(Permission.OrgBrandingWrite);
    }
}
