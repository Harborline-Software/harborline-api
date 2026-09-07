namespace Harborline.Api.UIAdapters.Blazor.Enums;

/// <summary>
/// Toolbar density for <see cref="Harborline.Api.UIAdapters.Blazor.Components.Media.HarborlinePdfViewer"/>.
/// </summary>
public enum PdfToolbarMode
{
    /// <summary>No toolbar chrome — document area fills the component.</summary>
    Hidden,

    /// <summary>Minimal toolbar — download only.</summary>
    Compact,

    /// <summary>Full toolbar — page nav, zoom, download.</summary>
    Full,
}
