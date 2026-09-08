using Microsoft.AspNetCore.Components;

namespace Harborline.Api.UIAdapters.Blazor.Components.Buttons;

/// <summary>An action exposed by <see cref="HarborlineDropDownButton"/>.</summary>
public sealed class DropDownButtonItem
{
    /// <summary>Visible action label.</summary>
    public required string Text { get; init; }

    /// <summary>Optional icon rendered before <see cref="Text"/>.</summary>
    public RenderFragment? Icon { get; init; }

    /// <summary>Whether this action is unavailable.</summary>
    public bool Disabled { get; init; }
}
