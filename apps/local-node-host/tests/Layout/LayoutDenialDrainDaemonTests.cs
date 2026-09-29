using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Kernel.Audit;
using Harborline.Api.LocalNodeHost.Layout;
using Harborline.Blocks.LayoutRuntime;

using static Harborline.Api.LocalNodeHost.Tests.Layout.LayoutDenialTestKit;

namespace Harborline.Api.LocalNodeHost.Tests.Layout;

/// <summary>
/// T-735, CodeRabbit review: <see cref="LayoutDenialDrainDaemon.DrainAsync"/> bounds its own wait by the
/// stopping token without cancelling an append already running, so a large backlog or a stalled append
/// cannot delay shutdown past the token's deadline.
/// </summary>
public sealed class LayoutDenialDrainDaemonTests
{
    [Fact(DisplayName = "T-735: DrainAsync stops waiting on a stalled append once its token cancels, without cancelling or duplicating the append itself")]
    public async Task DrainAsyncStopsWaitingOnCancellationButLeavesTheAppendRunning()
    {
        // The trail's append stalls until the test releases it, on no caller's token, so the stall lands
        // squarely on the append RunAsync is already running, never on the outbox's own listing. No clock
        // decides the verdict: DrainAsync must return while the append is provably still stalled.
        var trail = new StalledAppendTrail();
        using var p = new Pipeline(trail);
        var trace = p.Trace();
        Resolve(new OutcomeSources(LayoutRelatedResult.Denied(
            "authorization.permission_required", "/records/party-19", Owner)),
            trace, new LayoutResolutionRequest("request-7", "principal.clerk-4"));
        await trace.WrittenAsync();
        // The outbox write is durable; the append has not run yet.
        Assert.Single(await p.Outbox.ListUnresolvedAsync());

        var daemon = new LayoutDenialDrainDaemon(p.Outbox, p.Appender, TimeProvider.System, NullLogger<LayoutDenialDrainDaemon>.Instance);
        using var cts = new CancellationTokenSource();
        var drain = daemon.DrainAsync(cts.Token);
        await trail.Entered.WaitAsync(HangGuard);
        cts.Cancel();
        // DrainAsync returned on the token, not the append: the append has still not finished.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => drain.WaitAsync(HangGuard));
        Assert.Empty(await RowsAsync(trail.Inner));
        trail.Release();

        // The append itself was never cancelled (RunAsync uses no token) and finishes on its own; a later
        // drain (this daemon's next tick, or the reader path) safely resumes it via the same in-flight join,
        // never duplicating it, because LayoutDenialAppender's _inFlight dictionary is still holding it.
        await p.Appender.IdleAsync();
        Assert.Single(await RowsAsync(trail.Inner));
        Assert.Empty(await p.Outbox.ListUnresolvedAsync());
    }

    /// <summary>Only stops a broken DrainAsync hanging the run; generous, and never the verdict.</summary>
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    /// <summary>A gate log whose append waits for <see cref="Release"/>, on no caller's cancellation
    /// token — it always finishes, just not until the test says so.</summary>
    private sealed class StalledAppendTrail : IAuditTrail
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public InMemoryAuditTrail Inner { get; } = new();

        /// <summary>Completes once an append is running and stalled.</summary>
        public Task Entered => _entered.Task;

        public void Release() => _released.TrySetResult();

        public async ValueTask AppendAsync(AuditRecord record, CancellationToken ct = default)
        {
            _entered.TrySetResult();
            await _released.Task.ConfigureAwait(false);
            await Inner.AppendAsync(record, CancellationToken.None).ConfigureAwait(false);
        }

        public IAsyncEnumerable<AuditRecord> QueryAsync(AuditQuery query, CancellationToken ct = default)
            => Inner.QueryAsync(query, ct);
    }
}
