using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Governance.Consent;

namespace Harborline.Api.LocalNodeHost.Data.Governance;

/// <summary>
/// The scheduled expiry sweep for tenant consent records (ticket 213, ledger L646): a
/// <see cref="BackgroundService"/> that moves every stored <c>Active</c> record whose effective window has
/// closed to <c>Expired</c>, through the gate's own <see cref="TenantConsentGate.ExpireAsync"/>, so the
/// transition is persisted and audited like any other.
/// </summary>
/// <remarks>
/// <para>
/// <b>It changes no authority.</b> <see cref="TenantConsentRecord.StateAt"/> already reads a past-due
/// <c>Active</c> row as expired at the point of use, so nothing was ever authorized by a window that had
/// closed and this sweep cannot make anything safer. What it fixes is the RECORD: without it a stored row
/// stays <c>Active</c> on disk forever after its window closes, so a report over the raw store overcounts
/// active consents and the trail never says when the consent ended.
/// </para>
/// <para>
/// <b>Idempotent.</b> The sweep asks the same question the gate asks — <c>StateAt(now)</c> — and only moves
/// a record that is STORED active. A second run over the same store sees the records it already expired as
/// <c>Expired</c> and does nothing, so no record is expired twice and no duplicate audit row is written.
/// </para>
/// <para>
/// <b>The kernel clock decides.</b> The instant comes from the injected <see cref="TimeProvider"/> and is
/// read ONCE per sweep, so every record in one pass is judged and dated against the same instant
/// (ticket 216's discipline; the sweep has no request to inherit one from).
/// </para>
/// <para>
/// <b>Sweep exceptions are logged, not fatal</b> (mirrors <see cref="Harborline.Api.LocalNodeHost.Data.AssetRegistry.FormSubmitProjectionReconcilerDaemon"/>):
/// a durable loop must not die because one sweep threw.
/// </para>
/// </remarks>
public sealed class ConsentExpirySweepDaemon : BackgroundService
{
    /// <summary>The actor a sweep-initiated expiry is audited as — no human asked for it.</summary>
    public const string SweepPrincipal = "system:consent-expiry-sweep";

    /// <summary>The default sweep interval when none is configured.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(15);

    private readonly TenantConsentGate _gate;
    private readonly ITenantConsentStore _store;
    private readonly TimeProvider _time;
    private readonly ILogger<ConsentExpirySweepDaemon> _logger;
    private readonly TimeSpan _interval;

    /// <summary>Constructs the daemon over the consent gate, its store, the node clock, and a logger.</summary>
    /// <param name="interval">Sweep interval; defaults to <see cref="DefaultInterval"/> when null.</param>
    public ConsentExpirySweepDaemon(
        TenantConsentGate gate,
        ITenantConsentStore store,
        TimeProvider time,
        ILogger<ConsentExpirySweepDaemon> logger,
        TimeSpan? interval = null)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _interval = interval is { } i && i > TimeSpan.Zero ? i : DefaultInterval;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval, _time);
        // The `do` body runs BEFORE the first tick — a startup sweep catches every window that closed while
        // the node was off, then the periodic sweep follows.
        do
        {
            try
            {
                await SweepAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // shutdown
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Consent expiry sweep failed; will retry next interval.");
            }
        }
        while (await SafeWaitAsync(timer, stoppingToken).ConfigureAwait(false));
    }

    /// <summary>
    /// One sweep: expire every stored-active record whose window has closed, and nothing else. Returns how
    /// many records it moved. Public so a test can drive a single sweep deterministically.
    /// </summary>
    public async Task<int> SweepAsync(CancellationToken ct = default)
    {
        var at = _time.GetUtcNow();
        var actor = new ActorId(SweepPrincipal);
        var expired = 0;
        foreach (var tenant in await _store.TenantsAsync(ct).ConfigureAwait(false))
        {
            foreach (var record in await _store.ReadAsync(tenant, ct).ConfigureAwait(false))
            {
                // The SAME reading of "effective" the gate decides by; a record that is not stored active
                // has nothing to move, and one whose window is still open reads Active and is left alone.
                if (record.State != ConsentLifecycleState.Active
                    || record.StateAt(at) != ConsentLifecycleState.Expired)
                {
                    continue;
                }

                await _gate.ExpireAsync(record, actor, at, ct).ConfigureAwait(false);
                expired++;
            }
        }

        if (expired > 0)
        {
            _logger.LogInformation(
                "Consent expiry sweep expired {Count} consent record(s) whose effective window had closed.",
                expired);
        }

        return expired;
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
