using Harborline.Api.UIAdapters.Blazor.Base;
using Microsoft.AspNetCore.Components;

namespace Harborline.Api.UIAdapters.Blazor.Components.LocalFirst;

/// <summary>
/// Displays host-projected team state without depending on the host's runtime implementation.
/// </summary>
public partial class HarborlineTeamSwitcher : HarborlineComponentBase
{
    private ITeamSwitcherState? _subscribedState;

    /// <summary>The host-composed state projected into the dropdown.</summary>
    [Parameter, EditorRequired]
    public ITeamSwitcherState State { get; set; } = default!;

    /// <summary>Fired after the host-composed state finishes switching teams.</summary>
    [Parameter]
    public EventCallback<string> OnTeamChanged { get; set; }

    /// <summary>
    /// Fired when the user clicks the "Add team" button. The host owns the join flow.
    /// </summary>
    [Parameter]
    public EventCallback OnAddTeamRequested { get; set; }

    private string Classes => CombineClasses("sf-team-switcher");

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        if (ReferenceEquals(_subscribedState, State))
        {
            return;
        }

        if (_subscribedState is not null)
        {
            _subscribedState.Changed -= OnStateChanged;
        }

        _subscribedState = State;
        _subscribedState.Changed += OnStateChanged;
    }

    private async Task HandleTeamChangedAsync(string? teamId)
    {
        if (teamId is null)
        {
            return;
        }

        await State.SelectTeamAsync(teamId, CancellationToken.None).ConfigureAwait(false);
        if (OnTeamChanged.HasDelegate)
        {
            await OnTeamChanged.InvokeAsync(teamId).ConfigureAwait(false);
        }
    }

    private Task HandleAddClickAsync() =>
        OnAddTeamRequested.HasDelegate
            ? OnAddTeamRequested.InvokeAsync()
            : Task.CompletedTask;

    private static string FormatCount(int count) => count >= 100 ? "99+" : count.ToString(System.Globalization.CultureInfo.CurrentCulture);

    private void OnStateChanged(object? sender, EventArgs e) =>
        _ = InvokeAsync(StateHasChanged);

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && _subscribedState is not null)
        {
            _subscribedState.Changed -= OnStateChanged;
            _subscribedState = null;
        }

        base.Dispose(disposing);
    }
}
