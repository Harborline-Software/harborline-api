using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Harborline.Api.UIAdapters.Blazor.Internal.Interop;

internal sealed class PopupPositionService : IPopupPositionService
{
    private readonly IHarborlineJsModuleLoader _loader;

    public PopupPositionService(IHarborlineJsModuleLoader loader)
    {
        _loader = loader;
    }

    public async ValueTask<PopupPositionResult> ComputePositionAsync(
        ElementReference anchor,
        ElementReference popup,
        PopupAnchorOptions options,
        CancellationToken cancellationToken = default)
    {
        var module = await _loader.ImportAsync("js/harborline-positioning.js", cancellationToken);
        return await module.InvokeAsync<PopupPositionResult>("computePosition", cancellationToken,
            anchor, popup, new
            {
                placement = PlacementToJs(options.Placement),
                offset = options.Offset,
                autoFlip = options.AutoFlip,
                viewportMargin = options.ViewportMargin
            });
    }

    private static string PlacementToJs(PopupPlacement placement) => placement switch
    {
        PopupPlacement.Top => "top",
        PopupPlacement.TopStart => "top-start",
        PopupPlacement.TopEnd => "top-end",
        PopupPlacement.Bottom => "bottom",
        PopupPlacement.BottomStart => "bottom-start",
        PopupPlacement.BottomEnd => "bottom-end",
        PopupPlacement.Left => "left",
        PopupPlacement.LeftStart => "left-start",
        PopupPlacement.LeftEnd => "left-end",
        PopupPlacement.Right => "right",
        PopupPlacement.RightStart => "right-start",
        PopupPlacement.RightEnd => "right-end",
        _ => "bottom"
    };
}
