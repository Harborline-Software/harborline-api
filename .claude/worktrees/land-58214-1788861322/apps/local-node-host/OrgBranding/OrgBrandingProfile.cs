namespace Harborline.Api.LocalNodeHost.OrgBranding;

/// <summary>Which logo variant a request targets — the primary (light-safe) logo or the optional dark
/// variant used on dark surfaces (design §2.3).</summary>
public enum LogoVariant
{
    /// <summary>The primary logo — must read on both light and dark surfaces (design §2.3 primary path).</summary>
    Light = 0,

    /// <summary>The optional second upload used on dark surfaces when the primary reads poorly there.</summary>
    Dark = 1,
}

/// <summary>
/// The tenant-scoped org-branding profile (tenant-branding design §2.1, slice T1) — presentation config, NOT
/// authorization or identity-of-record. It hangs off the existing tenant (the onboarding org) as a thin
/// instance-cascade overlay: unset fields fall back to the platform default via
/// <see cref="IOrgBrandingResolver"/>. A solo founder who never opens Settings has no row at all and resolves
/// to the platform default with zero ceremony (time respect).
/// </summary>
/// <param name="TenantId">The owning tenant id (the active-team-derived data tenant; one profile per tenant).</param>
/// <param name="DisplayName">The org name — reuses the onboarding "tenant label"; null until set.</param>
/// <param name="LogoRef">The primary logo blob CID (<see cref="Harborline.Api.Foundation.Blobs.Cid"/> string), or
/// null for the wordmark fallback.</param>
/// <param name="LogoDarkRef">The optional dark-variant logo blob CID, or null.</param>
/// <param name="AccentColor">The resolved (post-AA, possibly clamped) accent hex <c>#RRGGBB</c>, or null.</param>
/// <param name="AccentForeground">The derived black/white foreground that reads at AA on the accent, or null.</param>
/// <param name="UpdatedAt">The last write instant (UTC).</param>
/// <param name="UpdatedBy">The server-derived principal that made the last write (audit doctrine).</param>
public sealed record OrgBrandingProfile(
    string TenantId,
    string? DisplayName,
    string? LogoRef,
    string? LogoDarkRef,
    string? AccentColor,
    string? AccentForeground,
    DateTimeOffset UpdatedAt,
    string UpdatedBy)
{
    /// <summary>The blob CID for the requested <paramref name="variant"/> (null when that variant is unset).</summary>
    public string? LogoRefFor(LogoVariant variant) =>
        variant == LogoVariant.Dark ? LogoDarkRef : LogoRef;
}
