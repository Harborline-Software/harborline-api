namespace Harborline.Api.LocalNodeHost.OrgBranding;

/// <summary>
/// The v1 tenant-branding guardrail constants (tenant-branding design note 2026-07-07, slice T1). These
/// encode the CIC v1 scope guardrails as data so every consumer (validator, resolver, routes) reads ONE
/// source: raster-only logo formats, the size/dimension caps, and the two representative surface luminances
/// the accent must remain legible against.
/// </summary>
/// <remarks>
/// <para>
/// <b>Raster-only (design §2.2 / §8 Q2, CIC ruling).</b> v1 accepts PNG / WebP / JPEG only. An uploaded SVG
/// is an XSS/DoS vector (embedded script / foreignObject / external refs / billion-laughs) and is rejected;
/// sanitized-SVG is an explicit fast-follow, NOT a v1 keystone dependency.
/// </para>
/// <para>
/// <b>Surface luminances (accent AA floor, design §2.4).</b> The tenant accent is a NARROW identity-only mark
/// (beacon / "new" / identity flourish), never the interactive primary or the functional-state palette. It
/// must stay legible on both the light and dark app surfaces; <see cref="AccentAaDeriver"/> uses these two
/// representative surface luminances to compute the accept band and clamp a failing pick. The exact Harborline App
/// surface tokens are refined in the T2/T3 chrome slices; these mirror <c>#FFFFFF</c> and a near-black
/// <c>#0B0B0C</c> and are deliberately conservative.
/// </para>
/// </remarks>
public static class OrgBrandingDefaults
{
    /// <summary>Maximum accepted logo size in bytes (512 KB, design §2.2).</summary>
    public const int MaxLogoBytes = 512 * 1024;

    /// <summary>Maximum accepted logo long-edge in pixels (1024 px, design §2.2).</summary>
    public const int MaxLogoLongEdgePx = 1024;

    /// <summary>The relative luminance of the light app surface (<c>#FFFFFF</c>).</summary>
    public const double LightSurfaceLuminance = 1.0;

    /// <summary>
    /// The relative luminance of the dark app surface (a near-black <c>#0B0B0C</c>). Computed once as a
    /// constant so the accent accept-band is deterministic and testable.
    /// </summary>
    public const double DarkSurfaceLuminance = 0.0056; // WCAG relative luminance of #0B0B0C

    /// <summary>
    /// The contrast floor the accent (a small NON-TEXT identity mark) must clear against each surface — the
    /// WCAG 1.4.11 non-text / graphical-object floor (3:1). Text ON the accent uses the stricter 4.5:1 AA
    /// floor via the derived black/white foreground.
    /// </summary>
    public const double AccentSurfaceContrastFloor = 3.0;
}
