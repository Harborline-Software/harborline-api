using Microsoft.AspNetCore.Components;
using Harborline.Api.UIAdapters.Blazor.Base;

namespace Harborline.Api.UIAdapters.Blazor.Components.Layout.Dock;

/// <summary>
/// Canonical MVP dock-manager surface for Harborline Blazor. Arranges child
/// <see cref="HarborlineDockSplit"/> and <see cref="HarborlineDockPane"/> elements
/// into a nested flex layout. Drag-to-dock, splitter resize handles, and
/// persistence are deferred (see component-mapping.json: status "partial").
/// </summary>
public partial class HarborlineDockManager : HarborlineComponentBase
{
    /// <summary>Nested dock tree (splits and panes).</summary>
    [Parameter] public RenderFragment? ChildContent { get; set; }

    /// <summary>CSS width of the dock manager container (e.g. <c>"100%"</c>).</summary>
    [Parameter] public string? Width { get; set; } = "100%";

    /// <summary>CSS height of the dock manager container (e.g. <c>"400px"</c>).</summary>
    [Parameter] public string? Height { get; set; } = "400px";

    /// <summary>Fired whenever the layout tree changes (pane closed, proportions updated, child moved).</summary>
    [Parameter] public EventCallback<DockLayoutEventArgs> OnLayoutChanged { get; set; }

    /// <summary>Cancellable callback fired before a pane is closed.</summary>
    [Parameter] public EventCallback<DockPaneCloseEventArgs> OnPaneClosed { get; set; }

    /// <summary>Cancellable callback fired when a pane is moved between containers.</summary>
    [Parameter] public EventCallback<DockPaneMovedEventArgs> OnPaneMoved { get; set; }

    private readonly List<HarborlineDockPane> _panes = new();

    private string HeightStyle() => string.IsNullOrWhiteSpace(Height) ? string.Empty : $"height:{Height}";
    private string WidthStyle() => string.IsNullOrWhiteSpace(Width) ? string.Empty : $"width:{Width}";

    internal async Task RegisterPaneAsync(HarborlineDockPane pane)
    {
        if (!_panes.Contains(pane))
        {
            _panes.Add(pane);
            await InvokeAsync(StateHasChanged);
        }
    }

    internal async Task UnregisterPaneAsync(HarborlineDockPane pane)
    {
        if (_panes.Remove(pane))
        {
            await NotifyLayoutChangedAsync(pane.ResolvedId, "pane-removed");
            await InvokeAsync(StateHasChanged);
        }
    }

    internal async Task<bool> RequestCloseAsync(HarborlineDockPane pane)
    {
        var args = new DockPaneCloseEventArgs
        {
            PaneId = pane.ResolvedId,
            Title = pane.Title,
        };
        await OnPaneClosed.InvokeAsync(args);
        if (args.IsCancelled) return false;

        await UnregisterPaneAsync(pane);
        return true;
    }

    internal Task NotifyLayoutChangedAsync(string? nodeId, string reason)
        => OnLayoutChanged.InvokeAsync(new DockLayoutEventArgs
        {
            ChangedNodeId = nodeId,
            Reason = reason,
        });

    /// <summary>
    /// Signals a pane move (tab reorder, cross-split move). Currently a
    /// placeholder for future drag-to-dock wiring: the MVP surface does
    /// not yet mutate the tree, but consumers can still veto via the
    /// cancellable callback.
    /// </summary>
    internal async Task<bool> RequestPaneMoveAsync(string paneId, string? fromContainerId, string? toContainerId)
    {
        var args = new DockPaneMovedEventArgs
        {
            PaneId = paneId,
            FromContainerId = fromContainerId,
            ToContainerId = toContainerId,
        };
        await OnPaneMoved.InvokeAsync(args);
        return !args.IsCancelled;
    }
}
