namespace Harborline.Api.LocalNodeHost.Data.OrgBranding;

/// <summary>
/// The flat EF row backing <c>OrgBrandingProfile</c> — one row per tenant (tenant-branding slice T1). Mapped
/// by <see cref="NodeLocalOrgBrandingDbContext"/> into the <c>org_branding</c> table in the SQLCipher-encrypted
/// local-node store. Node-exclusive presentation config — deliberately NOT a shared <c>IHarborlineEntityModule</c>,
/// so it stays out of the council C2 both-provider parity check.
/// </summary>
public sealed class OrgBrandingRow
{
    /// <summary>The owning tenant id (primary key — one branding profile per tenant).</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>The org display name (the onboarding tenant label); null until set.</summary>
    public string? DisplayName { get; set; }

    /// <summary>The primary logo blob CID string; null for the wordmark fallback.</summary>
    public string? LogoRef { get; set; }

    /// <summary>The optional dark-variant logo blob CID string; null when unset.</summary>
    public string? LogoDarkRef { get; set; }

    /// <summary>The resolved (post-AA, possibly clamped) accent hex <c>#RRGGBB</c>; null when unset.</summary>
    public string? AccentColor { get; set; }

    /// <summary>The derived black/white AA foreground for the accent; null when unset.</summary>
    public string? AccentForeground { get; set; }

    /// <summary>The last write instant (persisted as Unix epoch-milliseconds for integer-orderable storage).</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>The server-derived principal that made the last write (audit doctrine).</summary>
    public string UpdatedBy { get; set; } = string.Empty;
}
