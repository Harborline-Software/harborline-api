using Microsoft.JSInterop;

namespace Harborline.Api.UIAdapters.Blazor.Internal.Interop;

internal sealed class ClipboardService : IClipboardService
{
    private readonly IHarborlineJsModuleLoader _loader;

    public ClipboardService(IHarborlineJsModuleLoader loader)
    {
        _loader = loader;
    }

    public async ValueTask WriteAsync(ClipboardWriteRequest request, CancellationToken cancellationToken = default)
    {
        var module = await _loader.ImportAsync("js/harborline-clipboard-download.js", cancellationToken);
        await module.InvokeVoidAsync("writeClipboard", cancellationToken, request);
    }

    public async ValueTask<string> ReadTextAsync(CancellationToken cancellationToken = default)
    {
        var module = await _loader.ImportAsync("js/harborline-clipboard-download.js", cancellationToken);
        return await module.InvokeAsync<string>("readText", cancellationToken);
    }
}
