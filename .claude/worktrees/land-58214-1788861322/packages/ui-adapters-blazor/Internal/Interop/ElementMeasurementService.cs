using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Harborline.Api.UIAdapters.Blazor.Internal.Interop;

internal sealed class ElementMeasurementService : IElementMeasurementService
{
    private readonly IHarborlineJsModuleLoader _loader;

    public ElementMeasurementService(IHarborlineJsModuleLoader loader)
    {
        _loader = loader;
    }

    public async ValueTask<ElementRect> GetBoundingClientRectAsync(ElementReference element, CancellationToken cancellationToken = default)
    {
        var module = await _loader.ImportAsync("js/harborline-measurement.js", cancellationToken);
        return await module.InvokeAsync<ElementRect>("getBoundingClientRect", cancellationToken, element);
    }

    public async ValueTask<ViewportRect> GetViewportAsync(CancellationToken cancellationToken = default)
    {
        var module = await _loader.ImportAsync("js/harborline-measurement.js", cancellationToken);
        return await module.InvokeAsync<ViewportRect>("getViewport", cancellationToken);
    }

    public async ValueTask<double[]> GetChildWidthsAsync(ElementReference element, CancellationToken cancellationToken = default)
    {
        var module = await _loader.ImportAsync("js/harborline-measurement.js", cancellationToken);
        return await module.InvokeAsync<double[]>("getChildWidths", cancellationToken, element);
    }
}
