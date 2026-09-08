using System.Collections.Concurrent;
using Microsoft.JSInterop;

namespace Harborline.Api.UIAdapters.Blazor.Internal.Interop;

/// <summary>
/// Lazily imports and caches JS ES modules from this assembly's own static-web-asset path.
/// </summary>
internal sealed class HarborlineJsModuleLoader : IHarborlineJsModuleLoader, IDisposable
{
    /// <summary>
    /// <c>./_content/&lt;AssemblyName&gt;/</c> for the EMITTED assembly name, so the URL follows the
    /// assembly identity instead of a literal that silently 404s when the identity moves.
    /// </summary>
    private static readonly string ContentPrefix =
        $"./_content/{typeof(HarborlineJsModuleLoader).Assembly.GetName().Name}/";

    /// <summary>
    /// Resolves a wwwroot-relative module path (e.g. <c>js/harborline-a11y.js</c>) to its static-web-asset
    /// URL. Call sites that own their module lifetime import through this instead of a literal URL.
    /// </summary>
    internal static string Resolve(string modulePath) => ContentPrefix + modulePath;

    private readonly IJSRuntime _jsRuntime;
    private readonly ConcurrentDictionary<string, Task<IJSObjectReference>> _modules = new();
    private bool _disposed;

    public HarborlineJsModuleLoader(IJSRuntime jsRuntime)
    {
        _jsRuntime = jsRuntime;
    }

    public async ValueTask<IJSObjectReference> ImportAsync(string modulePath, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var fullPath = Resolve(modulePath);
        var task = _modules.GetOrAdd(fullPath, path =>
            _jsRuntime.InvokeAsync<IJSObjectReference>("import", cancellationToken, path).AsTask());

        return await task;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _modules.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var kvp in _modules)
        {
            try
            {
                if (kvp.Value.IsCompletedSuccessfully)
                {
                    var module = kvp.Value.Result;
                    await module.DisposeAsync();
                }
            }
            catch (JSDisconnectedException)
            {
                // Circuit disconnected; safe to ignore.
            }
        }

        _modules.Clear();
    }
}
