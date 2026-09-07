using Microsoft.JSInterop;

namespace Harborline.Api.UIAdapters.Blazor.Internal.Interop;

internal sealed class DownloadService : IDownloadService
{
    private readonly IHarborlineJsModuleLoader _loader;

    public DownloadService(IHarborlineJsModuleLoader loader)
    {
        _loader = loader;
    }

    public async ValueTask DownloadAsync(DownloadRequest request, CancellationToken cancellationToken = default)
    {
        var module = await _loader.ImportAsync("js/harborline-clipboard-download.js", cancellationToken);
        await module.InvokeVoidAsync("download", cancellationToken, request);
    }
}
