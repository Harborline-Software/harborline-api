using System.Globalization;

namespace Harborline.Api.LocalNodeHost.OrgBranding;

/// <summary>
/// The result of resolving a candidate tenant-accent color through the AA gate
/// (<see cref="AccentAaDeriver.Resolve"/>).
/// </summary>
/// <param name="Accepted">True when a usable accent was produced (either as-is or clamped).</param>
/// <param name="ResolvedAccent">The final accent hex (<c>#RRGGBB</c>, upper-case) — equal to the input when
/// in-band, or the nearest AA-passing tint when <see cref="WasClamped"/>. Null when rejected.</param>
/// <param name="Foreground">The derived black (<c>#000000</c>) or white (<c>#FFFFFF</c>) that reads at AA on
/// top of <see cref="ResolvedAccent"/> — the foreground pairing for a chip/badge label. Null when rejected.</param>
/// <param name="WasClamped">True when the input could not sit legibly on both surfaces and was nudged to the
/// nearest AA-passing tint (design §2.4 "nudged darker/lighter").</param>
/// <param name="ContrastOnLight">Contrast ratio of <see cref="ResolvedAccent"/> against the light surface.</param>
/// <param name="ContrastOnDark">Contrast ratio of <see cref="ResolvedAccent"/> against the dark surface.</param>
/// <param name="ForegroundContrast">Contrast ratio of <see cref="Foreground"/> on <see cref="ResolvedAccent"/>.</param>
/// <param name="RejectionReason">A plain-language reason when <see cref="Accepted"/> is false; else null.</param>
public sealed record AccentResolution(
    bool Accepted,
    string? ResolvedAccent,
    string? Foreground,
    bool WasClamped,
    double ContrastOnLight,
    double ContrastOnDark,
    double ForegroundContrast,
    string? RejectionReason);

/// <summary>
/// Pure, dependency-free AA derivation / clamp / reject helper for the single tenant accent color
/// (tenant-branding design §2.4, slice T1). It keeps the accent inside token discipline so a tenant can add
/// identity seasoning WITHOUT being able to ship an unreadable color.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two AA relationships (design §2.4).</b> (1) The accent is validated as a NON-TEXT identity mark against
/// the light AND dark app surfaces at the 3:1 floor; a pick that is too light (fails on light) or too dark
/// (fails on dark) is <em>clamped</em> — its luminance is moved to the nearest edge of the accept band by an
/// exact blend toward black (darken) or white (lighten), preserving hue. (2) A black/white
/// <em>foreground</em> is derived for text ON the accent at the stricter 4.5:1 AA text floor. Because the
/// better of black/white always clears ~4.58:1 for any color, the foreground pairing is always derivable;
/// the reject path is reserved for a genuinely invalid hex input.
/// </para>
/// <para>
/// <b>Deterministic + closed-form.</b> Luminance is a linear combination of linear-light RGB, so darkening
/// (scale toward black) and lightening (blend toward white) hit an exact target luminance with no clipping
/// and no search — the result is stable across runs and platforms, which is what makes it unit-testable
/// against known failing picks.
/// </para>
/// </remarks>
public static class AccentAaDeriver
{
    private const string Black = "#000000";
    private const string White = "#FFFFFF";

    /// <summary>
    /// Resolve a candidate accent. Returns an accepted resolution (as-is or clamped) for any valid hex, or a
    /// rejection with a plain reason for an unparseable input.
    /// </summary>
    public static AccentResolution Resolve(string? inputHex)
    {
        if (!TryParseHex(inputHex, out var r, out var g, out var b))
        {
            return new AccentResolution(
                Accepted: false, ResolvedAccent: null, Foreground: null, WasClamped: false,
                ContrastOnLight: 0, ContrastOnDark: 0, ForegroundContrast: 0,
                RejectionReason: "not-a-valid-hex-color");
        }

        // Work in linear-light space so luminance is a plain weighted sum.
        var lr = SrgbToLinear(r);
        var lg = SrgbToLinear(g);
        var lb = SrgbToLinear(b);
        var luminance = RelativeLuminance(lr, lg, lb);

        // The accept band: the accent's luminance range that clears the 3:1 non-text floor against BOTH
        // surfaces at once (too light => fails on the light surface; too dark => fails on the dark surface).
        var lMax = (OrgBrandingDefaults.LightSurfaceLuminance + 0.05)
                   / OrgBrandingDefaults.AccentSurfaceContrastFloor - 0.05;
        var lMin = OrgBrandingDefaults.AccentSurfaceContrastFloor
                   * (OrgBrandingDefaults.DarkSurfaceLuminance + 0.05) - 0.05;

        var wasClamped = false;
        if (luminance > lMax)
        {
            // Too light for the light surface — darken (scale linear RGB toward black). Luminance scales
            // linearly with the factor, so s = lMax / luminance hits the target exactly.
            var s = lMax / luminance;
            lr *= s; lg *= s; lb *= s;
            wasClamped = true;
        }
        else if (luminance < lMin)
        {
            // Too dark for the dark surface — lighten (blend toward white). Luminance' = L*(1-t)+t, so
            // t = (lMin - L)/(1 - L) hits the target exactly and never clips.
            var t = (lMin - luminance) / (1.0 - luminance);
            lr = lr * (1 - t) + t;
            lg = lg * (1 - t) + t;
            lb = lb * (1 - t) + t;
            wasClamped = true;
        }

        var rr = LinearToSrgb(lr);
        var rg = LinearToSrgb(lg);
        var rb = LinearToSrgb(lb);
        var resolved = $"#{rr:X2}{rg:X2}{rb:X2}";

        // Contrasts of the resolved accent vs each surface + the derived foreground on the accent.
        var accentLum = RelativeLuminance(SrgbToLinear(rr), SrgbToLinear(rg), SrgbToLinear(rb));
        var contrastLight = Contrast(OrgBrandingDefaults.LightSurfaceLuminance, accentLum);
        var contrastDark = Contrast(accentLum, OrgBrandingDefaults.DarkSurfaceLuminance);

        var contrastBlack = Contrast(accentLum, 0.0);       // black foreground on the accent
        var contrastWhite = Contrast(1.0, accentLum);       // white foreground on the accent
        var foreground = contrastBlack >= contrastWhite ? Black : White;
        var foregroundContrast = Math.Max(contrastBlack, contrastWhite);

        return new AccentResolution(
            Accepted: true,
            ResolvedAccent: resolved,
            Foreground: foreground,
            WasClamped: wasClamped,
            ContrastOnLight: Math.Round(contrastLight, 3),
            ContrastOnDark: Math.Round(contrastDark, 3),
            ForegroundContrast: Math.Round(foregroundContrast, 3),
            RejectionReason: null);
    }

    // ── color math ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Parse <c>#RGB</c> / <c>#RRGGBB</c> (with or without the leading <c>#</c>) into 0-255 channels.</summary>
    private static bool TryParseHex(string? hex, out int r, out int g, out int b)
    {
        r = g = b = 0;
        if (string.IsNullOrWhiteSpace(hex))
        {
            return false;
        }

        var s = hex.Trim();
        if (s.StartsWith('#'))
        {
            s = s[1..];
        }

        if (s.Length == 3)
        {
            // Expand shorthand #abc -> #aabbcc.
            s = $"{s[0]}{s[0]}{s[1]}{s[1]}{s[2]}{s[2]}";
        }

        if (s.Length != 6)
        {
            return false;
        }

        return TryHexByte(s, 0, out r) && TryHexByte(s, 2, out g) && TryHexByte(s, 4, out b);
    }

    private static bool TryHexByte(string s, int offset, out int value) =>
        int.TryParse(
            s.AsSpan(offset, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);

    /// <summary>sRGB 0-255 channel -> linear-light 0..1 (WCAG 2.x transfer function).</summary>
    private static double SrgbToLinear(int channel)
    {
        var c = channel / 255.0;
        return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    /// <summary>linear-light 0..1 -> sRGB 0-255 channel (inverse transfer, clamped + rounded).</summary>
    private static int LinearToSrgb(double linear)
    {
        var c = Math.Clamp(linear, 0.0, 1.0);
        var srgb = c <= 0.0031308 ? c * 12.92 : 1.055 * Math.Pow(c, 1.0 / 2.4) - 0.055;
        return (int)Math.Round(Math.Clamp(srgb, 0.0, 1.0) * 255.0);
    }

    private static double RelativeLuminance(double lr, double lg, double lb) =>
        0.2126 * lr + 0.7152 * lg + 0.0722 * lb;

    private static double Contrast(double lumA, double lumB)
    {
        var hi = Math.Max(lumA, lumB);
        var lo = Math.Min(lumA, lumB);
        return (hi + 0.05) / (lo + 0.05);
    }
}
