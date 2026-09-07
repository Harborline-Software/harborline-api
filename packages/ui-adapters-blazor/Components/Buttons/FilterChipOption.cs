namespace Harborline.Api.UIAdapters.Blazor.Components.Buttons;

/// <summary>A selectable option for <see cref="HarborlineFilterChips"/>.</summary>
public sealed record FilterChipOption(string Value, string Label, int? Count = null)
{
    /// <summary>Display text — the label plus the optional count badge.</summary>
    public string Text => Count is { } count ? $"{Label} ({count})" : Label;
}
