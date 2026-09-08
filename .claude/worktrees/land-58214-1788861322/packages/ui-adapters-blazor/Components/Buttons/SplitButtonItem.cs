namespace Harborline.Api.UIAdapters.Blazor.Components.Buttons;

/// <summary>An alternate action exposed by <see cref="HarborlineSplitButton"/>.</summary>
public sealed class SplitButtonItem
{
    /// <summary>Visible action label.</summary>
    public required string Label { get; init; }

    /// <summary>Whether this action is unavailable.</summary>
    public bool Disabled { get; init; }
}
