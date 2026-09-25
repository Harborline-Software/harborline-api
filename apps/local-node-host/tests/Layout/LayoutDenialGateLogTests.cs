using Microsoft.Extensions.Diagnostics.HealthChecks;

using Harborline.Api.Kernel.Audit;
using Harborline.Api.LocalNodeHost.Data.Layout;
using Harborline.Api.LocalNodeHost.Layout;
using Harborline.Blocks.LayoutRuntime;

using static Harborline.Api.LocalNodeHost.Tests.Layout.LayoutDenialTestKit;

namespace Harborline.Api.LocalNodeHost.Tests.Layout;

/// <summary>
/// T-731 — DES-0052 layout-eng-31 (host half) and layout-run-5: the host's <see cref="ILayoutDecisionTrace"/>
/// puts each related-binding denial in the durable local outbox at the act, and the appender moves it into
/// the authorization gate log as one signed record (owner ruling 2 of 2026-09-24: never throw, never lose).
/// </summary>
public sealed class LayoutDenialGateLogTests
{
    private static readonly LayoutRelatedDenial Expected = new("request-7", "principal.clerk-4", "owner-card",
        LayoutBindingKinds.Static, "invoice.owner", Owner, "authorization.permission_required", "/records/party-19");

    private static LayoutRelatedResult Denied => LayoutRelatedResult.Denied("authorization.permission_required", "/records/party-19", Owner);

    [Fact(DisplayName = "layout-eng-31, layout-run-5: a denied related binding is in the durable outbox at the act, and its append writes exactly one gate-log record carrying every field")]
    public async Task ADeniedRelatedBindingIsDurableAtTheActAndAppendsExactlyOneRecord()
    {
        var trail = new InMemoryAuditTrail();
        using var p = new Pipeline(trail);
        var trace = p.Trace();

        Resolve(new OutcomeSources(Denied), trace, new LayoutResolutionRequest("request-7", "principal.clerk-4"));
        await trace.WrittenAsync();

        // Durable before any append runs.
        var (entry, state) = Assert.Single(await p.Outbox.ListUnresolvedAsync());
        Assert.Equal(LayoutDenialOutboxState.Pending, state);
        Assert.Equal((Tenant, At, Expected), (entry.Tenant, entry.OccurredAt, entry.Denial));
        Assert.Empty(await RowsAsync(trail));

        await trace.AppendAsync();

        var record = Assert.Single(await RowsAsync(trail));
        Assert.Equal(entry.Id, record.AuditId);
        Assert.Equal(LayoutDenialGateLog.LayoutRelatedDeniedEventType, record.EventType);
        Assert.Equal(At, record.OccurredAt);
        Assert.Equal("principal.clerk-4", record.Actor?.Value);
        Assert.Equal(("record", "party-19"), (record.Target?.RecordKind, record.Target?.RecordId));
        Assert.Equal("records:read", record.Act?.Operation.Value);
        Assert.Equal(Expected, LayoutDenialGateLog.Denial(record));
        Assert.Empty(await p.Outbox.ListUnresolvedAsync());
    }

    [Fact(DisplayName = "layout-eng-31: a missing related target writes nothing to the outbox or the gate log")]
    public async Task AMissingRelatedTargetWritesNothing()
    {
        var trail = new InMemoryAuditTrail();
        using var p = new Pipeline(trail);
        var trace = p.Trace();

        Resolve(new OutcomeSources(LayoutRelatedResult.Absent), trace, new LayoutResolutionRequest("request-7", "principal.clerk-4"));
        await trace.WrittenAsync();
        await trace.AppendAsync();

        Assert.Empty(await p.Outbox.ListUnresolvedAsync());
        Assert.Empty(await RowsAsync(trail));
    }

    [Fact(DisplayName = "layout-run-5: a failed gate-log append never throws, stays in the outbox, raises the AU-5 health alert, and a drain appends it exactly once")]
    public async Task AFailedAppendStaysInTheOutboxAlertsAndIsDrainedOnce()
    {
        var trail = new SwitchableTrail { Down = true };
        using var p = new Pipeline(trail);
        var trace = p.Trace();

        Resolve(new OutcomeSources(Denied), trace, new LayoutResolutionRequest("request-7", "principal.clerk-4"));
        await trace.WrittenAsync();
        await trace.AppendAsync();

        Assert.Empty(await RowsAsync(trail));
        Assert.Equal(LayoutDenialOutboxState.Failed, Assert.Single(await p.Outbox.ListUnresolvedAsync()).State);
        var alert = await p.HealthAsync();
        Assert.Equal(HealthStatus.Degraded, alert.Status);
        Assert.Equal(1, alert.Data["failedAppends"]);

        trail.Down = false;
        await p.Appender.DrainAsync();
        await p.Appender.DrainAsync();

        Assert.Equal(Expected, LayoutDenialGateLog.Denial(Assert.Single(await RowsAsync(trail))));
        Assert.Empty(await p.Outbox.ListUnresolvedAsync());
        Assert.Equal(HealthStatus.Healthy, (await p.HealthAsync()).Status);
    }

    [Fact(DisplayName = "layout-run-5: a crash between the gate-log append and its outbox mark does not append the denial twice")]
    public async Task ACrashBetweenAppendAndMarkDoesNotAppendTwice()
    {
        var trail = new InMemoryAuditTrail();
        using var first = new Pipeline(trail);
        var trace = first.Trace();
        Resolve(new OutcomeSources(Denied), trace, new LayoutResolutionRequest("request-7", "principal.clerk-4"));
        await trace.WrittenAsync();
        var (entry, _) = Assert.Single(await first.Outbox.ListUnresolvedAsync());
        await trace.AppendAsync();

        // The same entry, still Pending in a store whose mark never landed, over the same gate log.
        using var restarted = new Pipeline(trail);
        await restarted.Outbox.EnqueueAsync(entry);
        await restarted.Appender.DrainAsync();

        Assert.Single(await RowsAsync(trail));
        Assert.Empty(await restarted.Outbox.ListUnresolvedAsync());
    }

    [Fact(DisplayName = "layout-run-5: a failed outbox write never throws and raises the AU-5 health alert")]
    public async Task AFailedOutboxWriteNeverThrowsAndAlerts()
    {
        using var p = new Pipeline(new InMemoryAuditTrail());
        var trace = p.Trace();
        p.Db.Fault = true;

        Resolve(new OutcomeSources(Denied), trace, new LayoutResolutionRequest("request-7", "principal.clerk-4"));
        await trace.WrittenAsync();
        await trace.AppendAsync();

        p.Db.Fault = false;
        var alert = await p.HealthAsync();
        Assert.Equal(HealthStatus.Degraded, alert.Status);
        Assert.Equal(1L, alert.Data["outboxWriteFailures"]);
    }
}
