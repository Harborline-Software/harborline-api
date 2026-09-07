using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Blocks.FinancialLedger.Data;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Workflow;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Workflow;

/// <summary>
/// ADR 0135 A0 — REAL ITERATION + the send-back MAX-ITERATIONS CAP, against the REAL recoverable
/// <see cref="LocalNodeDbContext"/> on an on-disk SQLite file (the production path). Drives the invoice handler
/// THROUGH the dispatcher with the REAL host context, exercising the bounded send-back loop the shipped code
/// had no terminator for:
/// <list type="bullet">
///   <item>each send-back round-trip BUMPS the instance's durable iteration counter, persisted on the instance
///     row and READ BACK on resume (never recomputed — bug-1337 class), so each re-entered <c>decide</c> /
///     <c>approve</c> pass gets a DISTINCT, crash-stable idempotency key;</item>
///   <item>a final approve after N send-backs posts EXACTLY ONE JE (the bumped-iteration post key derives a
///     distinct deterministic JE id — no collision with iteration-0, no double-post);</item>
///   <item>a crash MID-LOOP (after a send-back bumped the counter) resumes from the durable counter — the
///     resumed pass re-derives the SAME bumped key, no double-effect (Risk 3 / bug-1337 class);</item>
///   <item>a PATHOLOGICAL send-back loop TERMINATES at the configurable cap by escalating the instance to a
///     terminal Failed state instead of ping-ponging forever (the shipped unbounded-loop gap, bug-1353).</item>
/// </list>
/// </summary>
public sealed class NodeWorkflowIterationTests : IAsyncLifetime
{
    private string _dir = null!;
    private string _dbPath = null!;
    private static readonly TenantId LocalTenantId = new("local");

    public async Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "adr0135-a0-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "local-node.db");

        var factory = NewFactory();
        await using var ctx = await factory.CreateDbContextAsync();
        await ctx.Database.EnsureCreatedAsync();
    }

    public Task DisposeAsync()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort */ }
        return Task.CompletedTask;
    }

    private IDbContextFactory<LocalNodeDbContext> NewFactory()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHarborlineEntityModule, FinancialLedgerEntityModule>();
        services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialPeriods.Data.FinancialPeriodsEntityModule>();
        services.AddSingleton<IHarborlineEntityModule, WorkflowEntityModule>();
        services.AddDbContextFactory<LocalNodeDbContext>(o => o.UseSqlite($"Data Source={_dbPath};Pooling=False"));
        return services.BuildServiceProvider().GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
    }

    private static NodeEfWorkflowStore NewStore(IDbContextFactory<LocalNodeDbContext> factory)
        => new(factory);

    private static IWorkflowStepHandler InvoiceHandler()
        => new InvoiceApprovalHandler(
            NodeWorkflowDefinitions.InvoiceApprovalThresholdTable(),
            new NodeInvoiceApprovalContext());

    private async Task<int> CountJournalEntriesAsync(IDbContextFactory<LocalNodeDbContext> factory)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        return await ctx.Set<JournalEntry>().CountAsync(j => j.TenantId == LocalTenantId);
    }

    private async Task SeedOverThresholdInstanceAsync(IWorkflowStore store, string id)
        => await store.CreateInstanceAsync(new WorkflowInstanceRecord
        {
            Id = id,
            TenantId = LocalTenantId.Value,
            DefinitionKey = InvoiceApprovalSteps.DefinitionKey,
            DefinitionVersion = NodeWorkflowDefinitions.InvoiceApprovalV1Version,
            CurrentStep = InvoiceApprovalSteps.Decide,
            Status = WorkflowStatus.Running,
            StateJson = "{\"amount\":7500,\"debitAccount\":\"1100\",\"creditAccount\":\"4000\",\"memo\":\"a0 test\"}",
        });

    /// <summary>One send-back round-trip: resume the parked approve with send-back (→ loop-back park on decide,
    /// iteration bumps), then re-drive the decide trigger (→ forward re-park on approve). Mirrors the production
    /// cutover's send-back round-trip.</summary>
    private static async Task<WorkflowDispatchResult> SendBackRoundTripAsync(
        WorkflowTriggerDispatcher dispatcher, string id)
    {
        var back = await dispatcher.DispatchAsync(WorkflowTrigger.For(
            WorkflowTriggerKind.HumanAction, id, InvoiceApprovalSteps.Approve, "{\"decision\":\"send-back\"}"));
        Assert.Equal(WorkflowDispatchResult.Parked, back);
        return await dispatcher.DispatchAsync(
            WorkflowTrigger.For(WorkflowTriggerKind.Event, id, InvoiceApprovalSteps.Decide));
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  REAL ITERATION — send-back bumps a DURABLE counter, read back on resume
    // ═════════════════════════════════════════════════════════════════════════

    [Fact(DisplayName = "A0: a send-back round-trip BUMPS the instance's durable iteration counter (persisted on the instance row), and a RESTART reads it back verbatim — never recomputed")]
    public async Task SendBack_BumpsDurableIteration_ReadBackOnRestart()
    {
        var factory = NewFactory();
        var store = NewStore(factory);
        await SeedOverThresholdInstanceAsync(store, "it-1");
        var dispatcher = new WorkflowTriggerDispatcher(store, new[] { InvoiceHandler() });

        // decide → park on approve (iteration 0).
        await dispatcher.DispatchAsync(WorkflowTrigger.For(WorkflowTriggerKind.Event, "it-1", InvoiceApprovalSteps.Decide));
        Assert.Equal(0, (await store.LoadAsync("it-1"))!.Iteration);

        // One send-back round-trip → iteration bumps to 1, re-parked on approve.
        var afterFirst = await SendBackRoundTripAsync(dispatcher, "it-1");
        Assert.Equal(WorkflowDispatchResult.Parked, afterFirst);
        Assert.Equal(1, (await store.LoadAsync("it-1"))!.Iteration);
        Assert.Equal(InvoiceApprovalSteps.Approve, (await store.LoadAsync("it-1"))!.CurrentStep);

        // A second send-back round-trip → iteration 2.
        await SendBackRoundTripAsync(dispatcher, "it-1");
        Assert.Equal(2, (await store.LoadAsync("it-1"))!.Iteration);

        // RESTART (fresh factory + store over the same db file) → the counter is read back from the durable
        // instance row, not recomputed from the event log.
        var store2 = NewStore(NewFactory());
        Assert.Equal(2, (await store2.LoadAsync("it-1"))!.Iteration);
    }

    [Fact(DisplayName = "A0: each send-back iteration writes a DISTINCT (instance, iteration, step) idempotency key — the re-entered approve at iter N is not falsely deduped against iter 0")]
    public async Task DistinctIdempotencyKey_PerIteration()
    {
        var factory = NewFactory();
        var store = NewStore(factory);
        await SeedOverThresholdInstanceAsync(store, "it-keys");
        var dispatcher = new WorkflowTriggerDispatcher(store, new[] { InvoiceHandler() });

        await dispatcher.DispatchAsync(WorkflowTrigger.For(WorkflowTriggerKind.Event, "it-keys", InvoiceApprovalSteps.Decide));
        await SendBackRoundTripAsync(dispatcher, "it-keys");   // iteration → 1
        await SendBackRoundTripAsync(dispatcher, "it-keys");   // iteration → 2

        // Approve at iteration 2 → posts EXACTLY ONE JE (the bumped-iteration post key derives a distinct,
        // deterministic JE id; no collision with an iteration-0 key, no double-post).
        var approved = await dispatcher.DispatchAsync(WorkflowTrigger.For(
            WorkflowTriggerKind.HumanAction, "it-keys", InvoiceApprovalSteps.Approve, "{\"decision\":\"approve\"}"));
        Assert.Equal(WorkflowDispatchResult.Advanced, approved);
        Assert.Equal(1, await CountJournalEntriesAsync(factory));

        // The advance that committed the post was the `approve` trigger at the BUMPED iteration (2): the
        // dispatcher keys the idempotency row on the TRIGGER's step (`approve`) + the durable iteration, so the
        // row lands at (it-keys, 2, approve). This is the real-iteration key threading end to end — the
        // re-entered approve at iter 2 wrote a DISTINCT key from any iter-0/1 pass (the JE-layer
        // source-reference unique index is the deterministic backstop independent of this row).
        await using var ctx = await factory.CreateDbContextAsync();
        var approveIterations = await ctx.Set<WorkflowStepIdempotencyRecord>()
            .Where(r => r.InstanceId == "it-keys" && r.Step == InvoiceApprovalSteps.Approve)
            .Select(r => r.Iteration)
            .ToListAsync();
        Assert.Equal(new[] { 2 }, approveIterations);
    }

    [Fact(DisplayName = "A0 (Risk 3 / bug-1337 class): a CRASH mid-loop (after a send-back bumped the counter) resumes from the DURABLE counter — the resumed approve re-derives the SAME bumped key, posts EXACTLY ONE JE (no double-post)")]
    public async Task CrashMidLoop_ResumesFromDurableCounter_NoDoublePost()
    {
        var factory = NewFactory();
        var store = NewStore(factory);
        await SeedOverThresholdInstanceAsync(store, "it-crash");
        var dispatcher = new WorkflowTriggerDispatcher(store, new[] { InvoiceHandler() });

        await dispatcher.DispatchAsync(WorkflowTrigger.For(WorkflowTriggerKind.Event, "it-crash", InvoiceApprovalSteps.Decide));
        await SendBackRoundTripAsync(dispatcher, "it-crash");   // iteration → 1; parked on approve at iter 1

        // "CRASH": a fresh factory/store/dispatcher over the same db file — the in-memory dispatcher state is
        // gone, only the durable store survives. The counter (1) is read back from the instance row.
        var factory2 = NewFactory();
        var store2 = NewStore(factory2);
        var dispatcher2 = new WorkflowTriggerDispatcher(store2, new[] { InvoiceHandler() });
        Assert.Equal(1, (await store2.LoadAsync("it-crash"))!.Iteration);

        // Resume: approve at the read-back iteration 1 → posts EXACTLY ONE JE.
        var approved = await dispatcher2.DispatchAsync(WorkflowTrigger.For(
            WorkflowTriggerKind.HumanAction, "it-crash", InvoiceApprovalSteps.Approve, "{\"decision\":\"approve\"}"));
        Assert.Equal(WorkflowDispatchResult.Advanced, approved);
        Assert.Equal(1, await CountJournalEntriesAsync(factory2));

        // A redelivered approve at the same iteration is an idempotent no-op (instance terminal) — still one JE.
        var redelivered = await dispatcher2.DispatchAsync(WorkflowTrigger.For(
            WorkflowTriggerKind.HumanAction, "it-crash", InvoiceApprovalSteps.Approve, "{\"decision\":\"approve\"}"));
        Assert.Equal(WorkflowDispatchResult.Terminal, redelivered);
        Assert.Equal(1, await CountJournalEntriesAsync(factory2));
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  THE CAP — a pathological send-back loop terminates by escalation
    // ═════════════════════════════════════════════════════════════════════════

    [Fact(DisplayName = "A0 (bug-1353): a PATHOLOGICAL send-back loop TERMINATES at the configurable max-iterations cap — the instance escalates to a terminal Failed state instead of looping forever, and posts NO JE")]
    public async Task PathologicalSendBackLoop_TerminatesAtCap_Escalates()
    {
        var factory = NewFactory();
        var store = NewStore(factory);
        await SeedOverThresholdInstanceAsync(store, "it-runaway");

        // A small cap so the test runs a bounded number of round-trips. Cap = 3 ⇒ iterations 0,1,2 are allowed
        // loop-backs; the loop-back that WOULD reach iteration 3 escalates instead.
        var options = new WorkflowEngineOptions { MaxIterations = 3 };
        var dispatcher = new WorkflowTriggerDispatcher(store, new[] { InvoiceHandler() }, options);

        await dispatcher.DispatchAsync(WorkflowTrigger.For(WorkflowTriggerKind.Event, "it-runaway", InvoiceApprovalSteps.Decide));

        // Hammer send-back well past the cap — a pathological client that never approves/rejects. Without the
        // cap this would never terminate (the pre-A0 unbounded loop). With the cap, it MUST reach a terminal
        // state within MaxIterations round-trips.
        var terminalReached = false;
        for (var i = 0; i < 100; i++)
        {
            // The approve→send-back. The loop-back park that hits the cap ESCALATES (an atomic advance to the
            // terminal Failed state), so this returns Advanced at the cap; a send-back on an already-escalated
            // (terminal) instance returns Terminal. Either ends the loop.
            var back = await dispatcher.DispatchAsync(WorkflowTrigger.For(
                WorkflowTriggerKind.HumanAction, "it-runaway", InvoiceApprovalSteps.Approve, "{\"decision\":\"send-back\"}"));
            if (back is WorkflowDispatchResult.Advanced or WorkflowDispatchResult.Terminal)
            {
                terminalReached = true;
                break;
            }

            Assert.Equal(WorkflowDispatchResult.Parked, back);   // under the cap, the loop-back re-parks

            // Re-drive decide so the next round-trip's approve fires (the production cutover's round-trip).
            var redecide = await dispatcher.DispatchAsync(
                WorkflowTrigger.For(WorkflowTriggerKind.Event, "it-runaway", InvoiceApprovalSteps.Decide));
            if (redecide is WorkflowDispatchResult.Terminal or WorkflowDispatchResult.Advanced)
            {
                terminalReached = true;
                break;
            }
        }

        Assert.True(terminalReached, "the pathological send-back loop must terminate at the cap, not loop forever");

        var instance = await store.LoadAsync("it-runaway");
        Assert.Equal(WorkflowStatus.Failed, instance!.Status);                 // escalated, not Completed
        Assert.Equal(WorkflowTriggerDispatcher.EscalatedStep, instance.CurrentStep);
        Assert.Equal(0, await CountJournalEntriesAsync(factory));              // NO JE ever posted on escalation
    }

    [Fact(DisplayName = "A0: under the cap, a legitimate small number of send-backs followed by approve still posts EXACTLY ONE JE (the cap does not break the normal round-trip)")]
    public async Task UnderCap_LegitimateSendBacksThenApprove_PostsOnce()
    {
        var factory = NewFactory();
        var store = NewStore(factory);
        await SeedOverThresholdInstanceAsync(store, "it-ok");
        var options = new WorkflowEngineOptions { MaxIterations = 10 };
        var dispatcher = new WorkflowTriggerDispatcher(store, new[] { InvoiceHandler() }, options);

        await dispatcher.DispatchAsync(WorkflowTrigger.For(WorkflowTriggerKind.Event, "it-ok", InvoiceApprovalSteps.Decide));
        await SendBackRoundTripAsync(dispatcher, "it-ok");
        await SendBackRoundTripAsync(dispatcher, "it-ok");
        await SendBackRoundTripAsync(dispatcher, "it-ok");

        var approved = await dispatcher.DispatchAsync(WorkflowTrigger.For(
            WorkflowTriggerKind.HumanAction, "it-ok", InvoiceApprovalSteps.Approve, "{\"decision\":\"approve\"}"));
        Assert.Equal(WorkflowDispatchResult.Advanced, approved);

        var instance = await store.LoadAsync("it-ok");
        Assert.Equal(WorkflowStatus.Completed, instance!.Status);
        Assert.Equal(InvoiceApprovalSteps.Posted, instance.CurrentStep);
        Assert.Equal(1, await CountJournalEntriesAsync(factory));
    }
}
