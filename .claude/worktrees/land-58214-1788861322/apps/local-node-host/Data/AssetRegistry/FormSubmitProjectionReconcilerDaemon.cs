using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.Foundation.Forms.Submission;

namespace Harborline.Api.LocalNodeHost.Data.AssetRegistry;

/// <summary>
/// The <b>recovery half</b> of the post-submit projection outbox (ADR 0101 Rev 3.1 Wave 2b / F-RECON) —
/// a <see cref="BackgroundService"/> that runs the <see cref="IFormSubmitProjectionReconciler"/> on the
/// live node: a STARTUP DRAIN (the first sweep, before any interval elapses) plus a periodic sweep. It
/// re-runs every unresolved outbox row (<see cref="FormSubmitOutboxState.Pending"/> — a hard process
/// death mid-projection; <see cref="FormSubmitOutboxState.Failed"/> — a projection that threw) through the
/// real projector and marks it completed on success.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists (the F-RECON gate).</b> Wave 2b's live-capture wiring records a durable outbox row
/// BEFORE the projection runs, so a committed submission whose projection is interrupted leaves a
/// recoverable trace. But a durable row is only OPERATIONALLY recovered if something drains it on the
/// running node — without this daemon, "at-least-once delivery" was structural, not operational, and the
/// ONLY reachable recovery path was a client retry (the very double-submit path F-ROUTE closes). This
/// daemon makes recovery automatic: an interrupted projection heals on the node with no human and no
/// retry.
/// </para>
/// <para>
/// <b>At-least-once is safe.</b> A sweep may re-run a projection that already partially applied; the
/// shipped projector derives its side-record id deterministically (<c>ConditionAssessmentId.Derive</c>),
/// so a replay UPSERTs rather than duplicating. A row that throws again is left Failed (with the fresh
/// error) for the next sweep — never lost.
/// </para>
/// <para>
/// <b>Sweep exceptions are logged, not fatal</b> (mirrors <see cref="Harborline.Api.LocalNodeHost.Data.Workflow.WorkflowScheduleDaemon"/>):
/// a durable recovery loop must not die because one sweep threw. The reconciler already contains
/// per-row faults (a failed row stays Failed and is retried), so a throw escaping to here is unexpected —
/// it is logged and the daemon keeps ticking.
/// </para>
/// </remarks>
public sealed class FormSubmitProjectionReconcilerDaemon : BackgroundService
{
    private readonly IFormSubmitProjectionReconciler _reconciler;
    private readonly TimeProvider _time;
    private readonly ILogger<FormSubmitProjectionReconcilerDaemon> _logger;
    private readonly TimeSpan _interval;

    /// <summary>The default sweep interval when none is configured (a low-urgency recovery backstop).</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(60);

    /// <summary>Constructs the daemon over the reconciler, the node clock, and a logger.</summary>
    /// <param name="interval">Sweep interval; defaults to <see cref="DefaultInterval"/> when null.</param>
    public FormSubmitProjectionReconcilerDaemon(
        IFormSubmitProjectionReconciler reconciler,
        TimeProvider time,
        ILogger<FormSubmitProjectionReconcilerDaemon> logger,
        TimeSpan? interval = null)
    {
        _reconciler = reconciler ?? throw new ArgumentNullException(nameof(reconciler));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _interval = interval is { } i && i > TimeSpan.Zero ? i : DefaultInterval;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval, _time);
        // The `do` body runs BEFORE the first WaitForNextTickAsync — that first iteration is the STARTUP
        // DRAIN (heals any row left unresolved by a prior process death), then the periodic sweep follows.
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
                // Never let one sweep kill the recovery loop — log and keep ticking.
                _logger.LogError(ex, "Form-submit projection reconcile sweep failed; will retry next interval.");
            }
        }
        while (await SafeWaitAsync(timer, stoppingToken).ConfigureAwait(false));
    }

    /// <summary>
    /// One sweep: re-drain every unresolved outbox row through the reconciler. Public so a test can drive
    /// a single sweep deterministically without waiting on the timer.
    /// </summary>
    public async Task<int> SweepAsync(CancellationToken ct = default)
    {
        var recovered = await _reconciler.ReconcileAsync(ct).ConfigureAwait(false);
        if (recovered > 0)
        {
            _logger.LogInformation(
                "Form-submit projection reconcile sweep recovered {Count} interrupted submission projection(s).",
                recovered);
        }
        return recovered;
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
