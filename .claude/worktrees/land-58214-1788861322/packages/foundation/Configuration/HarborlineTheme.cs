namespace Harborline.Api.Foundation.Configuration;

/// <summary>
/// Immutable snapshot of a Harborline theme, including color palette, typography scale,
/// shape tokens, and layout direction. Passed to <c>IHarborlineThemeService</c>
/// (see <c>Harborline.Api.Foundation.Services</c>) to apply a new visual appearance at runtime.
/// </summary>
public record HarborlineTheme
{
    /// <summary>
    /// Gets the color palette (primary, secondary, surface, semantic colors, etc.).
    /// </summary>
    public HarborlineColorPalette Colors { get; init; } = new();

    /// <summary>
    /// Gets the typography scale (font families, sizes, weights, line heights).
    /// </summary>
    public HarborlineTypographyScale Typography { get; init; } = new();

    /// <summary>
    /// Gets the shape tokens (border radius values, elevation levels).
    /// </summary>
    public HarborlineShape Shape { get; init; } = new();

    /// <summary>
    /// Gets a value indicating whether the theme uses right-to-left layout direction.
    /// </summary>
    public bool IsRtl { get; init; }
}
