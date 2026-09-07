using Microsoft.JSInterop;

namespace Harborline.Api.UIAdapters.Blazor.Internal.Interop;

internal sealed class DropZoneService : IDropZoneService
{
    private readonly IHarborlineJsModuleLoader _loader;

    public DropZoneService(IHarborlineJsModuleLoader loader) => _loader = loader;

    public async ValueTask<int> RegisterAsync(string dropZoneElementId, string fileInputElementId, CancellationToken cancellationToken = default)
    {
        var module = await _loader.ImportAsync("js/harborline-dropzone.js", cancellationToken);
        return await module.InvokeAsync<int>("registerDropZone", cancellationToken, dropZoneElementId, fileInputElementId);
    }

    public async ValueTask UnregisterAsync(int handleId, CancellationToken cancellationToken = default)
    {
        var module = await _loader.ImportAsync("js/harborline-dropzone.js", cancellationToken);
        await module.InvokeVoidAsync("unregisterDropZone", cancellationToken, handleId);
    }
}
