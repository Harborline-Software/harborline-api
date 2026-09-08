using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Harborline.Api.LocalNodeHost;

/// <summary>
/// Bridges the existing executable composition into a same-process client host.
/// </summary>
/// <remarks>
/// The Harborline App still reaches the node through the shared loopback HTTP listener. This
/// bridge only changes who owns the process lifetime; it does not expose node services
/// as a direct-call API.
/// </remarks>
public static class LocalNodeHostRuntime
{
    private static readonly object Gate = new();
    private static TaskCompletionSource<Uri>? _started;
    private static TaskCompletionSource? _stopRequested;
    private static Task? _entrypoint;
    private static bool _externalLifecycle;

    /// <summary>Internal test-only access to the live node container for persistence assertions.</summary>
    internal static IServiceProvider? CurrentServices { get; private set; }

    /// <summary>Runs the node composition and returns after the node has bound its listener.</summary>
    public static Task<Uri> StartAsync(
        string sessionToken,
        string dataDirectory,
        CancellationToken cancellationToken,
        TimeProvider? kernelClock = null,
        Action<IServiceCollection>? finalServiceRegistration = null)
    {
        TaskCompletionSource<Uri> started;
        TaskCompletionSource stopRequested;
        lock (Gate)
        {
            if (_entrypoint is not null)
                throw new InvalidOperationException("The local-node host is already running.");

            _externalLifecycle = true;
            started = _started = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
            stopRequested = _stopRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        var entrypoint = global::LocalNodeHostComposition.RunAsync(
            Array.Empty<string>(), sessionToken, dataDirectory, kernelClock,
            finalServiceRegistration);
        lock (Gate) _entrypoint = entrypoint;
        _ = entrypoint.ContinueWith(
            task => CompleteCompositionFault(task, started, stopRequested),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return WaitForStartAsync(started, cancellationToken);
    }

    /// <summary>Stops the externally managed node and waits for its composition to unwind.</summary>
    public static async Task StopAsync(CancellationToken cancellationToken)
    {
        Task? entrypoint;
        lock (Gate)
        {
            entrypoint = _entrypoint;
            _stopRequested?.TrySetResult();
        }

        if (entrypoint is not null)
            await entrypoint.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Called by the executable composition root after it has built the host.</summary>
    internal static async Task RunAsync(
        IHost host,
        Health.SharedHostedWebApp? listener = null)
    {
        bool externallyManaged;
        lock (Gate) externallyManaged = _externalLifecycle;

        if (!externallyManaged)
        {
            try
            {
                await host.StartAsync().ConfigureAwait(false);
                listener?.CaptureSelectedUrl();

                var shutdown = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                using var stoppingRegistration = host.Services
                    .GetRequiredService<IHostApplicationLifetime>()
                    .ApplicationStopping
                    .Register(static state => ((TaskCompletionSource)state!).TrySetResult(), shutdown);

                await shutdown.Task.ConfigureAwait(false);
                await host.StopAsync().ConfigureAwait(false);
            }
            finally
            {
                await DisposeHostAsync(host).ConfigureAwait(false);
            }
            return;
        }

        try
        {
            await host.StartAsync().ConfigureAwait(false);
            CurrentServices = host.Services;
            var sharedApp = listener
                ?? host.Services.GetRequiredService<Health.SharedHostedWebApp>();
            sharedApp.CaptureSelectedUrl();
            var selectedUrl = sharedApp.SelectedUrl;
            if (string.IsNullOrWhiteSpace(selectedUrl))
                throw new InvalidOperationException("The local-node listener did not report a bound URL.");

            _started!.TrySetResult(new Uri(selectedUrl, UriKind.Absolute));
            await _stopRequested!.Task.ConfigureAwait(false);
            await host.StopAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _started?.TrySetException(exception);
            _stopRequested?.TrySetException(exception);
            throw;
        }
        finally
        {
            await DisposeHostAsync(host).ConfigureAwait(false);
            CurrentServices = null;
            lock (Gate)
            {
                _entrypoint = null;
                _started = null;
                _stopRequested = null;
                _externalLifecycle = false;
            }
        }
    }

    private static async Task<Uri> WaitForStartAsync(
        TaskCompletionSource<Uri> started,
        CancellationToken cancellationToken)
    {
        return await started.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void CompleteCompositionFault(
        Task entrypoint,
        TaskCompletionSource<Uri> started,
        TaskCompletionSource stopRequested)
    {
        var exception = entrypoint.Exception?.GetBaseException()
            ?? new InvalidOperationException("The local-node host composition failed.");
        started.TrySetException(exception);
        stopRequested.TrySetException(exception);

        lock (Gate)
        {
            if (ReferenceEquals(_started, started))
            {
                _entrypoint = null;
                _started = null;
                _stopRequested = null;
                _externalLifecycle = false;
                CurrentServices = null;
            }
        }
    }

    private static async ValueTask DisposeHostAsync(IHost host)
    {
        if (host is IAsyncDisposable asyncHost)
            await asyncHost.DisposeAsync().ConfigureAwait(false);
        else
            host.Dispose();
    }
}
