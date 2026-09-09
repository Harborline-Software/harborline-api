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
    private static CancellationTokenSource? _startupCancellation;
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
        CancellationTokenSource startupCancellation;
        lock (Gate)
        {
            if (_entrypoint is not null)
                throw new InvalidOperationException("The local-node host is already running.");

            _externalLifecycle = true;
            started = _started = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
            stopRequested = _stopRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            startupCancellation = _startupCancellation = new CancellationTokenSource();
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

        return WaitForStartAsync(
            entrypoint,
            started,
            stopRequested,
            startupCancellation,
            cancellationToken);
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
        TaskCompletionSource<Uri>? started;
        TaskCompletionSource? stopRequested;
        CancellationToken startupCancellation;
        lock (Gate)
        {
            externallyManaged = _externalLifecycle;
            started = _started;
            stopRequested = _stopRequested;
            startupCancellation = _startupCancellation?.Token ?? CancellationToken.None;
        }

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
            await host.StartAsync(startupCancellation).ConfigureAwait(false);
            CurrentServices = host.Services;
            var sharedApp = listener
                ?? host.Services.GetRequiredService<Health.SharedHostedWebApp>();
            sharedApp.CaptureSelectedUrl();
            var selectedUrl = sharedApp.SelectedUrl;
            if (string.IsNullOrWhiteSpace(selectedUrl))
                throw new InvalidOperationException("The local-node listener did not report a bound URL.");

            started!.TrySetResult(new Uri(selectedUrl, UriKind.Absolute));
            await stopRequested!.Task.ConfigureAwait(false);
            await host.StopAsync().ConfigureAwait(false);
        }
        finally
        {
            await DisposeHostAsync(host).ConfigureAwait(false);
            CurrentServices = null;
            ClearGeneration(started!);
        }
    }

    private static async Task<Uri> WaitForStartAsync(
        Task entrypoint,
        TaskCompletionSource<Uri> started,
        TaskCompletionSource stopRequested,
        CancellationTokenSource startupCancellation,
        CancellationToken cancellationToken)
    {
        try
        {
            return await started.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try
            {
                startupCancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // A simultaneous composition fault already released this generation.
            }

            stopRequested.TrySetResult();
            await entrypoint.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

            ClearGeneration(started);
            throw;
        }
    }

    private static void CompleteCompositionFault(
        Task entrypoint,
        TaskCompletionSource<Uri> started,
        TaskCompletionSource stopRequested)
    {
        var exception = entrypoint.Exception?.GetBaseException()
            ?? new InvalidOperationException("The local-node host composition failed.");
        ClearGeneration(started);

        // The caller must not observe the boot fault until this generation has released its
        // entrypoint. It may immediately start another host after observing the failure.
        stopRequested.TrySetException(exception);
        started.TrySetException(exception);
    }

    private static void ClearGeneration(TaskCompletionSource<Uri> started)
    {
        CancellationTokenSource? startupCancellation = null;
        lock (Gate)
        {
            if (ReferenceEquals(_started, started))
            {
                _entrypoint = null;
                _started = null;
                _stopRequested = null;
                startupCancellation = _startupCancellation;
                _startupCancellation = null;
                _externalLifecycle = false;
                CurrentServices = null;
            }
        }

        startupCancellation?.Dispose();
    }

    private static async ValueTask DisposeHostAsync(IHost host)
    {
        if (host is IAsyncDisposable asyncHost)
            await asyncHost.DisposeAsync().ConfigureAwait(false);
        else
            host.Dispose();
    }
}
