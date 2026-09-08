namespace Harborline.Api.LocalNodeHost.OrgBranding;

/// <summary>Which mark a tenant-lead slot should render, per the fallback ladder (design §2.5:
/// <c>org logo → org-name wordmark → platform default</c>).</summary>
public enum BrandingLogoSource
{
    /// <summary>An org logo is configured — render the uploaded mark (mode-appropriate).</summary>
    OrgLogo = 0,

    /// <summary>No logo, but an org name is set — render the typeset org-name wordmark (never an empty box).</summary>
    OrgNameWordmark = 1,

    /// <summary>Nothing is configured — render the Harborline platform default (pre-setup look).</summary>
    PlatformDefault = 2,
}

/// <summary>
/// The resolved branding a tenant-lead slot renders (design §2.5), keyed to the active tenant. This is the
/// backend "serve" shape: it carries the resolved fields plus the ladder decision, but it deliberately does
/// NOT carry the platform's brand LITERAL — the localized "Harborline" fallback label is a UI/i18n concern
/// supplied by the chrome slice when <see cref="LogoSource"/> is <see cref="BrandingLogoSource.PlatformDefault"/>
/// (keeps the domain-generic brand string out of the backend).
/// </summary>
/// <param name="HasOrgIdentity">True when the tenant has configured any org identity (name and/or logo).</param>
/// <param name="DisplayName">The org name when set; null when the platform default should be shown.</param>
/// <param name="LogoSource">The fallback-ladder decision for the logo slot.</param>
/// <param name="HasLightLogo">True when a primary logo blob is referenced.</param>
/// <param name="HasDarkLogo">True when an optional dark-variant logo blob is referenced.</param>
/// <param name="AccentColor">The resolved (AA-safe) accent hex, or null when unset.</param>
/// <param name="AccentForeground">The AA foreground pairing for the accent, or null when unset.</param>
public sealed record ResolvedOrgBranding(
    bool HasOrgIdentity,
    string? DisplayName,
    BrandingLogoSource LogoSource,
    bool HasLightLogo,
    bool HasDarkLogo,
    string? AccentColor,
    string? AccentForeground);

/// <summary>
/// Resolves the active tenant's branding through the fallback ladder (tenant-branding design §2.5, slice T1).
/// Resolution is keyed to the ACTIVE tenant/org context (not a global singleton), so when the active-org
/// switcher lands (deferred, design §5) branding follows the active org with no rework.
/// </summary>
public interface IOrgBrandingResolver
{
    /// <summary>Resolve the branding for the currently active tenant, applying the fallback ladder.</summary>
    Task<ResolvedOrgBranding> ResolveActiveAsync(CancellationToken ct = default);
}
