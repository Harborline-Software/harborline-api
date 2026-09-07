namespace Harborline.Api.UIAdapters.Blazor.Enums;

/// <summary>
/// Visual style applied to a <see cref="Harborline.Api.UIAdapters.Blazor.Components.Media.MapMarker"/>.
/// The MVP HarborlineMap renders these as an SVG preview fallback; the full JS-backed map engine
/// (Leaflet) is expected to map these values to real tile-pinned markers.
/// </summary>
public enum MapMarkerIcon
{
    /// <summary>Default pin glyph provided by the underlying map engine.</summary>
    Default,

    /// <summary>Teardrop pin marker.</summary>
    Pin,

    /// <summary>Simple filled circle.</summary>
    Circle,

    /// <summary>Custom marker — consumer supplies the icon markup via the engine's API.</summary>
    Custom,
}
