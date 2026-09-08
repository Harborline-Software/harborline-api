namespace Harborline.Api.UIAdapters.Blazor.Components.Navigation;

/// <summary>A single actionable entry shown in a <c>HarborlineCommandPalette</c>.</summary>
public sealed class CommandPaletteItem
{
    /// <summary>Stable unique id for the item.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Visible label of the command.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Optional secondary description shown under the label.</summary>
    public string? Description { get; set; }

    /// <summary>Optional group heading the item is listed under.</summary>
    public string? Group { get; set; }

    /// <summary>Extra keywords matched by the filter in addition to the label.</summary>
    public IReadOnlyList<string> Keywords { get; set; } = [];
}
