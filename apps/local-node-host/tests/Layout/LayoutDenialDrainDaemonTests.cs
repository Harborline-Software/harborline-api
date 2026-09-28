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
        // The trail's append is slow on a clock unrelated to the daemon's token (Task.Delay with no
        // cancellation), so the delay lands squarely on the append RunAsync is already running, never on
        // the outbox's own (fast) listing.
        var trail = new SlowAppendTrail(TimeSpan.FromMilliseconds(400));
        using var p = new Pipeline(trail);
        var trace = p.Trace();
        Resolve(new OutcomeSources(LayoutRelatedResult.Denied(
            "authorization.permission_required", "/records/party-19", Owner)),
            trace, new LayoutResolutionRequest("request-7", "principal.clerk-4"));
        await trace.WrittenAsync();
        // The outbox write is durable; the append has not run yet.
        Assert.Single(await p.Outbox.ListUnresolvedAsync());

        var daemon = new LayoutDenialDrainDaemon(p.Outbox, p.Appender, TimeProvider.System, NullLogger<LayoutDenialDrainDaemon>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => daemon.DrainAsync(cts.Token));
        // DrainAsync returned on the token's own deadline, not the append's — well short of the 400 ms delay.
        Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(300), $"DrainAsync waited {clock.Elapsed} for a cancelled token.");

        // The append itself was never cancelled (RunAsync uses no token) and finishes on its own; a later
        // drain (this daemon's next tick, or the reader path) safely resumes it via the same in-flight join,
        // never duplicating it, because LayoutDenialAppender's _inFlight dictionary is still holding it.
        await p.Appender.IdleAsync();
        Assert.Single(await RowsAsync(trail.Inner));
        Assert.Empty(await p.Outbox.ListUnresolvedAsync());
    }

    /// <summary>A gate log whose append takes <paramref name="delay"/>, on a clock no caller's
    /// cancellation token reaches — it always finishes, just not promptly.</summary>
    private sealed class SlowAppendTrail(TimeSpan delay) : IAuditTrail
    {
        public InMemoryAuditTrail Inner { get; } = new();

        public async ValueTask AppendAsync(AuditRecord record, CancellationToken ct = default)
        {
            await Task.Delay(delay, CancellationToken.None).ConfigureAwait(false);
            await Inner.AppendAsync(record, CancellationToken.None).ConfigureAwait(false);
        }

        public IAsyncEnumerable<AuditRecord> QueryAsync(AuditQuery query, CancellationToken ct = default)
            => Inner.QueryAsync(query, ct);
    }
}
