using System.Globalization;

using Harborline.Api.LocalNodeHost.OrgBranding;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.OrgBranding;

/// <summary>
/// Tenant-branding slice T1 — the accent AA derive/clamp/reject helper. Proves the gate: an accent that
/// cannot sit legibly on both surfaces is CLAMPED to the nearest AA-passing tint (never shipped failing), an
/// in-band accent passes through unchanged, and an invalid hex is REJECTED. Every accepted accent carries an
/// AA-readable black/white foreground.
/// </summary>
public sealed class AccentAaDeriverTests
{
    // ── in-band: accepted unchanged ──────────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "in-band mid-tone accent is accepted unchanged (not clamped)")]
    public void InBand_Accepted_NotClamped()
    {
        // #7A7A7A has relative luminance ~0.194, inside the [~0.117, 0.30] accept band for both surfaces.
        var r = AccentAaDeriver.Resolve("#7A7A7A");

        Assert.True(r.Accepted);
        Assert.False(r.WasClamped);
        Assert.Equal("#7A7A7A", r.ResolvedAccent);
        Assert.True(r.ContrastOnLight >= 3.0, $"expected >=3 on light, got {r.ContrastOnLight}");
        Assert.True(r.ContrastOnDark >= 3.0, $"expected >=3 on dark, got {r.ContrastOnDark}");
    }

    // ── too light: clamped darker ────────────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "too-light accent (yellow) is clamped darker to pass on the light surface")]
    public void TooLight_ClampedDarker()
    {
        // Pure yellow: luminance ~0.93 — invisible on white. Must clamp darker to clear the light surface.
        var r = AccentAaDeriver.Resolve("#FFFF00");

        Assert.True(r.Accepted);
        Assert.True(r.WasClamped);
        Assert.NotEqual("#FFFF00", r.ResolvedAccent);
        // At/above the 3:1 non-text floor on the light surface after clamp (integer-rounding tolerance).
        Assert.True(r.ContrastOnLight >= 2.95, $"expected ~>=3 on light after clamp, got {r.ContrastOnLight}");
    }

    // ── too dark: clamped lighter ────────────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "too-dark accent (deep blue) is clamped lighter to pass on the dark surface")]
    public void TooDark_ClampedLighter()
    {
        // A deep cobalt (~0.028 luminance) is invisible on the near-black dark surface. Clamp lighter.
        var r = AccentAaDeriver.Resolve("#0A2A6C");

        Assert.True(r.Accepted);
        Assert.True(r.WasClamped);
        Assert.NotEqual("#0A2A6C", r.ResolvedAccent);
        Assert.True(r.ContrastOnDark >= 2.95, $"expected ~>=3 on dark after clamp, got {r.ContrastOnDark}");
    }

    [Theory(DisplayName = "achromatic extremes clamp into the band")]
    [InlineData("#000000")]
    [InlineData("#FFFFFF")]
    public void AchromaticExtremes_Clamped(string hex)
    {
        var r = AccentAaDeriver.Resolve(hex);
        Assert.True(r.Accepted);
        Assert.True(r.WasClamped);
        Assert.True(r.ContrastOnLight >= 2.95);
        Assert.True(r.ContrastOnDark >= 2.95);
    }

    // ── foreground pairing ───────────────────────────────────────────────────────────────────────────────

    [Theory(DisplayName = "every accepted accent carries an AA-readable black/white foreground")]
    [InlineData("#06489C")]
    [InlineData("#7A7A7A")]
    [InlineData("#FFFF00")]
    [InlineData("#0A2A6C")]
    public void AcceptedAccent_HasAaForeground(string hex)
    {
        var r = AccentAaDeriver.Resolve(hex);

        Assert.True(r.Accepted);
        Assert.Contains(r.Foreground, new[] { "#000000", "#FFFFFF" });
        Assert.True(r.ForegroundContrast >= 4.5,
            $"on-accent text must clear AA 4.5:1, got {r.ForegroundContrast} for {hex}");
        AssertValidHex(r.ResolvedAccent!);
    }

    [Fact(DisplayName = "shorthand #abc hex parses")]
    public void ShorthandHex_Parses()
    {
        var r = AccentAaDeriver.Resolve("#5AF"); // == #55AAFF
        Assert.True(r.Accepted);
        AssertValidHex(r.ResolvedAccent!);
    }

    // ── rejection ────────────────────────────────────────────────────────────────────────────────────────

    [Theory(DisplayName = "invalid input is rejected with a reason, never a failing color")]
    [InlineData("not-a-color")]
    [InlineData("#12")]
    [InlineData("#GGGGGG")]
    [InlineData("")]
    [InlineData(null)]
    public void Invalid_Rejected(string? input)
    {
        var r = AccentAaDeriver.Resolve(input);

        Assert.False(r.Accepted);
        Assert.Null(r.ResolvedAccent);
        Assert.Null(r.Foreground);
        Assert.False(string.IsNullOrEmpty(r.RejectionReason));
    }

    private static void AssertValidHex(string hex)
    {
        Assert.StartsWith("#", hex);
        Assert.Equal(7, hex.Length);
        Assert.True(int.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _),
            $"'{hex}' is not a valid #RRGGBB");
    }
}
