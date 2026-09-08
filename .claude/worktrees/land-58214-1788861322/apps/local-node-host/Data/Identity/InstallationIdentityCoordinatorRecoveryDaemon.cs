using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Harborline.Api.LocalNodeHost.Data.Identity;

/// <summary>
/// Runs the installation identity-coordinator recovery drain once at startup and periodically after
/// that. A failure is logged and isolated from both host startup and later sweeps.
/// </summary>
internal sealed class InstallationIdentityCoordinatorRecoveryDaemon : BackgroundService
{
    private readonly InstallationIdentityCoordinatorRecoveryService _recovery;
    private readonly TimeProvider _time;
    private readonly ILogger<InstallationIdentityCoordinatorRecoveryDaemon> _logger;
    private readonly TimeSpan _interval;
    private readonly SemaphoreSlim _drainGate = new(1, 1);
    private readonly TaskCompletionSource _startupDrainCompleted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public InstallationIdentityCoordinatorRecoveryDaemon(
        InstallationIdentityCoordinatorRecoveryService recovery,
        TimeProvider time,
        ILogger<InstallationIdentityCoordinatorRecoveryDaemon> logger,
        TimeSpan interval)
    {
        _recovery = recovery ?? throw new ArgumentNullException(nameof(recovery));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _interval = interval > TimeSpan.Zero
            ? interval
            : TimeSpan.FromSeconds(IdentityCoordinatorOptions.DefaultRecoverySweepIntervalSeconds);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval, _time);
        do
        {
            try
            {
                await DrainOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            finally
            {
                _startupDrainCompleted.TrySetResult();
            }
        }
        while (await SafeWaitAsync(timer, stoppingToken).ConfigureAwait(false));
    }

    /// <summary>Completes after the startup drain yields, for deterministic hosted-service tests.</summary>
    internal Task StartupDrainCompleted => _startupDrainCompleted.Task;

    /// <summary>Runs one single-flight drain; exposed internally for deterministic host tests.</summary>
    internal async Task<IReadOnlyList<InstallationIdentityCoordinationResult>> DrainOnceAsync(
        CancellationToken cancellationToken = default)
    {
        if (!await _drainGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogDebug("Identity coordinator recovery drain already running; skipping overlapping pass.");
            return [];
        }

        try
        {
            return await _recovery.RecoverPendingAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Identity coordinator recovery drain failed; the host will continue and retry.");
            return [];
        }
        finally
        {
            _drainGate.Release();
        }
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public override void Dispose()
    {
        _drainGate.Dispose();
        base.Dispose();
    }
}
