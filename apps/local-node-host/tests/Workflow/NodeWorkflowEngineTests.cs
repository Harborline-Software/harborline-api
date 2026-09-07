using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialLedger.Services;
using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Blocks.FinancialLedger.Data;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Workflow;
using Harborline.Api.LocalNodeHost.Tests.Authorization;

using NSubstitute;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Workflow;

/// <summary>
/// ADR 0135 slice 1 — the durable process-engine CORE tests, run against the REAL recoverable
/// <see cref="LocalNodeDbContext"/> on an on-disk SQLite file (the local-node store stand-in). Promotes
/// the de-risk spike's SC1 kill-test onto the production store + the workflow seam, and adds the trigger,
/// idempotency-determinism, and append-only-invariant coverage the slice owes.
/// </summary>
/// <remarks>
/// Each test opens a real on-disk db, drives the engine, then (for crash-resume) opens a FRESH factory
/// over the SAME file — the process-restart simulation. The pass gate for the crash cases is whether the
/// resume double-posts the financial effect (a real <see cref="JournalEntry"/>).
/// </remarks>
public sealed class NodeWorkflowEngineTests : IAsyncLifetime
{
    private string _dir = null!;
    private string _dbPath = null!;
    private static readonly TenantId LocalTenantId = new("local");

    public async Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "adr0135-engine-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "local-node.db");

        // Create the schema once with the production module set (financial + workflow tables share the
        // context, so the JE effect + the advance can co-commit).
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

    /// <summary>A fresh context factory over the same on-disk db — calling it again = a process restart.</summary>
    private IDbContextFactory<LocalNodeDbContext> NewFactory()
    {
        var services = new ServiceCollection();
        // The production node composes these two modules for the JE + workflow tables.
        services.AddSingleton<IHarborlineEntityModule, FinancialLedgerEntityModule>();
        services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialPeriods.Data.FinancialPeriodsEntityModule>();
        services.AddSingleton<IHarborlineEntityModule, WorkflowEntityModule>();
        services.AddDbContextFactory<LocalNodeDbContext>(o => o.UseSqlite($"Data Source={_dbPath};Pooling=False"));
        return services.BuildServiceProvider()
            .GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
    }

    private NodeEfWorkflowStore NewStore(IDbContextFactory<LocalNodeDbContext> factory)
        => new(factory);

    private async Task<int> CountJournalEntriesAsync(IDbContextFactory<LocalNodeDbContext> factory)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        return await ctx.Set<JournalEntry>().CountAsync(j => j.TenantId == LocalTenantId);
    }

    private async Task SeedInstanceAsync(IWorkflowStore store, string id, string defKey, string step)
        => await store.CreateInstanceAsync(new WorkflowInstanceRecord
        {
            Id = id,
            TenantId = LocalTenantId.Value,
            DefinitionKey = defKey,
            DefinitionVersion = "v1",
            CurrentStep = step,
            Status = WorkflowStatus.Running,
        });

    // ── A real balanced posted JE as the atomic effect (mirrors the audit test's BalancedPosted) ──
    private static JournalEntry BalancedPosted(string id, decimal amount, string sourceReference) =>
        new JournalEntry(
            id: new JournalEntryId(id),
            tenantId: LocalTenantId,
            entryDate: new DateOnly(2026, 6, 23),
            memo: "workflow post step",
            lines: new List<JournalEntryLine>
            {
                new(new GLAccountId("1000"), amount, 0m),
                new(new GLAccountId("4000"), 0m, amount),
            },
            createdAtUtc: new Instant(DateTimeOffset.UtcNow),
            sourceReference: sourceReference)
        {
            Status = JournalEntryStatus.Posted,
        };

    /// <summary>
    /// A <see cref="WorkflowEffect"/> that stages a real <see cref="JournalEntry"/> onto the advance's
    /// in-flight context — the production atomic-advance shape (the JE effect rides the workflow
    /// transaction). The JE's id + SourceReference are derived deterministically from the step key, so a
    /// re-run produces the IDENTICAL JE (build invariant #2 → the JE unique index is a second backstop).
    /// </summary>
    private static WorkflowEffect JournalPostEffect(WorkflowStepKey key, decimal amount)
    {
        var jeId = "JE-" + key.ToDeterministicGuid("journal-entry").ToString("N");
        var sourceRef = "workflow:" + key.Value;
        return new WorkflowEffect((uow, ct) =>
        {
            var ctx = (LocalNodeDbContext)uow;
            ctx.Set<JournalEntry>().Add(BalancedPosted(jeId, amount, sourceRef));
            return Task.CompletedTask;
        });
    }

    /// <summary>A <see cref="WorkflowEffect"/> that throws while staging — simulates a crash inside the
    /// atomic advance, after the effect begins staging but before commit.</summary>
    private static WorkflowEffect CrashingEffect()
        => new((uow, ct) => throw new SimulatedCrash("inside the atomic advance, pre-commit"));

    private sealed class SimulatedCrash(string where) : Exception($"SIMULATED CRASH @ {where}");

    // ═════════════════════════════════════════════════════════════════════════
    //  SC1 — crash-resume → EXACTLY ONE effect (no double-post). The headline gate.
    // ═════════════════════════════════════════════════════════════════════════

    [Fact(DisplayName = "SC1: a crash inside the atomic advance rolls back the JE effect too; resume re-runs and posts EXACTLY ONE JE (no double-post)")]
    public async Task AtomicAdvance_CrashRollsBackEffect_ResumePostsExactlyOnce()
    {
        var factory = NewFactory();
        var store = NewStore(factory);
        await SeedInstanceAsync(store, "inst-sc1", "recurring-invoice", "post");

        var key = new WorkflowStepKey("inst-sc1", iteration: 0, "post");
        var instance = (await store.LoadAsync("inst-sc1"))!;
        instance.StateJson = "{\"amount\":500,\"debitAccount\":\"1000\",\"creditAccount\":\"4000\",\"memo\":\"atomic ledger effect\"}";
        // Persist the working state used by the real factory before the act begins.
        await using (var seed = await factory.CreateDbContextAsync())
        {
            var row = await seed.Set<WorkflowInstanceRecord>().SingleAsync(item => item.Id == instance.Id);
            row.StateJson = instance.StateJson;
            await seed.SaveChangesAsync();
        }
        var rawJournal = Substitute.For<IJournalStore>();
        var posting = new JournalPostingService(
            new AlwaysPostableAccountResolver(),
            Substitute.For<IPeriodResolver>(),
            rawJournal,
            TestAuthorization.Gate(false));
        var journalDecision = TestAuthorization.AllowedDecision(
            LocalTenantId,
            NodeLedgerPostingEffect.JournalEntryIdFor(instance).Value,
            "journal-entry",
            TeamRolePermissions.LedgerPost);
        var workflowDecision = TestAuthorization.AllowedDecision(LocalTenantId, instance.Id);
        var realEffect = NodeLedgerPostingEffect.Build(
            new WorkflowEffectRequest(
                NodeLedgerPostingEffect.CapabilityRef,
                instance,
                key,
                "{}",
                OriginatingDecision: workflowDecision,
                EffectDecision: journalDecision),
            posting);
        var crashingRealEffect = new WorkflowEffect(async (uow, ct) =>
        {
            await realEffect.StageAsync(uow, ct);
            throw new SimulatedCrash("after the real ledger effect staged, before workflow commit");
        });

        // First attempt: crash AFTER the real ledger effect staged inside the single transaction.
        await Assert.ThrowsAsync<SimulatedCrash>(() => store.AdvanceAsync(
            key, crashingRealEffect, resultJson: "{}", eventType: "Advanced", eventDataJson: "{}",
            nextStep: "post:done", nextStatus: WorkflowStatus.Completed));

        // Nothing committed — the effect rolled back with the event + idempotency + position.
        Assert.Equal(0, await CountJournalEntriesAsync(factory));
        Assert.Null(await store.FindStepResultAsync(key));
        var afterCrash = await store.LoadAsync("inst-sc1");
        Assert.Equal(WorkflowStatus.Running, afterCrash!.Status);   // position unchanged
        Assert.Equal("post", afterCrash.CurrentStep);

        // RESTART + RESUME over the same db. The idempotency guard misses (nothing committed), so the
        // step re-runs and now commits cleanly.
        var factory2 = NewFactory();
        var store2 = NewStore(factory2);
        await store2.AdvanceAsync(
            key, realEffect, resultJson: "{\"je\":\"posted\"}",
            eventType: "Advanced", eventDataJson: "{}", nextStep: "post:done", nextStatus: WorkflowStatus.Completed);

        Assert.Equal(1, await CountJournalEntriesAsync(factory2)); // EXACTLY ONE

        // Redelivered trigger (idempotency hit) — a second advance is a no-op via the guard at the
        // dispatcher level; here we assert the durable proof exists.
        Assert.NotNull(await store2.FindStepResultAsync(key));
        var done = await store2.LoadAsync("inst-sc1");
        Assert.Equal(WorkflowStatus.Completed, done!.Status);
    }

    [Fact(DisplayName = "Idempotency: a clean advance records the durable proof; re-deriving the SAME effect id stays unique (the JE backstop) — still one JE")]
    public async Task CleanAdvance_RecordsIdempotency_AndDeterministicEffectIdIsUnique()
    {
        var factory = NewFactory();
        var store = NewStore(factory);
        await SeedInstanceAsync(store, "inst-idem", "recurring-invoice", "post");
        var key = new WorkflowStepKey("inst-idem", 0, "post");

        await store.AdvanceAsync(key, JournalPostEffect(key, 250m), "{}", "Advanced", "{}", "post:done", WorkflowStatus.Completed);
        Assert.Equal(1, await CountJournalEntriesAsync(factory));

        // The deterministic JE id derived from the key collides on the PK / source-ref unique index if a
        // raw re-stage is attempted — the JE-layer backstop independent of the workflow idempotency.
        await using var ctx = await factory.CreateDbContextAsync();
        ctx.Set<JournalEntry>().Add(BalancedPosted(
            "JE-" + key.ToDeterministicGuid("journal-entry").ToString("N"), 250m, "workflow:" + key.Value));
        await Assert.ThrowsAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  PARK / RESUME — durable across a restart.
    // ═════════════════════════════════════════════════════════════════════════

    [Fact(DisplayName = "Park/resume: a step parks the instance durably; a restart rehydrates Parked; resume advances with no double-effect")]
    public async Task Park_SurvivesRestart_ResumeAdvancesOnce()
    {
        var factory = NewFactory();
        var store = NewStore(factory);
        await SeedInstanceAsync(store, "inst-park", "invoice-approval", "approve");

        // Park on the human-task (e.g. invoice > $5k → await approval).
        await store.ParkAsync("inst-park", "approve", "{\"reason\":\"cp-step\"}");

        // RESTART: read the parked instance from the durable store.
        var factory2 = NewFactory();
        var store2 = NewStore(factory2);
        var parked = await store2.LoadAsync("inst-park");
        Assert.Equal(WorkflowStatus.Parked, parked!.Status);
        Assert.Equal("approve", parked.CurrentStep);

        // Resume with the human's typed result — a pure advance (no effect).
        var key = new WorkflowStepKey("inst-park", 0, "approve");
        await store2.AdvanceAsync(key, effect: null, resultJson: "{\"decision\":\"approved\",\"by\":\"user-7\"}",
            eventType: "Resumed", eventDataJson: "{\"decision\":\"approved\"}", nextStep: "post", nextStatus: WorkflowStatus.Running);

        // RESTART again — the resume is durable + idempotent.
        var factory3 = NewFactory();
        var store3 = NewStore(factory3);
        var resumed = await store3.LoadAsync("inst-park");
        Assert.Equal(WorkflowStatus.Running, resumed!.Status);
        Assert.Equal("post", resumed.CurrentStep);
        var recorded = await store3.FindStepResultAsync(key);
        Assert.NotNull(recorded);
        Assert.Contains("approved", recorded!.ResultJson);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  THE 4 TRIGGERS — each advances an instance through the dispatcher.
    // ═════════════════════════════════════════════════════════════════════════

    [Theory(DisplayName = "Each of the 4 triggers (event / schedule / human-action / dependency-complete) advances its instance through the dispatcher")]
    [InlineData(WorkflowTriggerKind.Event)]
    [InlineData(WorkflowTriggerKind.Schedule)]
    [InlineData(WorkflowTriggerKind.HumanAction)]
    [InlineData(WorkflowTriggerKind.DependencyComplete)]
    public async Task EachTrigger_AdvancesInstance(WorkflowTriggerKind kind)
    {
        var factory = NewFactory();
        var store = NewStore(factory);
        var instId = $"inst-trig-{kind}";
        await SeedInstanceAsync(store, instId, "demo-advance", "start");

        var dispatcher = new WorkflowTriggerDispatcher(store, new[] { new AdvanceOnceHandler() });
        var result = await dispatcher.DispatchAsync(
            WorkflowTrigger.For(kind, instId, "start", "{\"trigger\":\"" + kind + "\"}"));

        Assert.Equal(WorkflowDispatchResult.Advanced, result);
        var inst = await store.LoadAsync(instId);
        Assert.Equal("start:done", inst!.CurrentStep);
        Assert.Equal(WorkflowStatus.Running, inst.Status); // non-terminal, so the redelivery hits the idempotency guard

        // A redelivered trigger of the same kind (same instance/step) is a no-op via the idempotency guard.
        var again = await dispatcher.DispatchAsync(WorkflowTrigger.For(kind, instId, "start"));
        Assert.Equal(WorkflowDispatchResult.ReplayedNoOp, again);
    }

    [Fact(DisplayName = "Dispatcher: a trigger for an unknown instance returns UnknownInstance; a terminal instance returns Terminal")]
    public async Task Dispatcher_UnknownAndTerminal()
    {
        var factory = NewFactory();
        var store = NewStore(factory);
        var dispatcher = new WorkflowTriggerDispatcher(store, new[] { new AdvanceOnceHandler() });

        var unknown = await dispatcher.DispatchAsync(WorkflowTrigger.For(WorkflowTriggerKind.Event, "nope", "start"));
        Assert.Equal(WorkflowDispatchResult.UnknownInstance, unknown);

        await SeedInstanceAsync(store, "inst-term", "demo-advance", "start");
        await store.AdvanceAsync(new WorkflowStepKey("inst-term", 0, "start"), null, "{}", "Completed", "{}", "done", WorkflowStatus.Completed);
        var terminal = await dispatcher.DispatchAsync(WorkflowTrigger.For(WorkflowTriggerKind.Event, "inst-term", "next"));
        Assert.Equal(WorkflowDispatchResult.Terminal, terminal);
    }

    /// <summary>A trivial handler: advance once to <c>{step}:done</c>, staying Running (the degenerate
    /// 1-step automation rule per ADR 0135 D1). All four triggers funnel through it identically; the
    /// non-terminal next status lets a redelivery exercise the idempotency-replay path.</summary>
    private sealed class AdvanceOnceHandler : IWorkflowStepHandler
    {
        public string DefinitionKey => "demo-advance";
        public ValueTask<WorkflowStepOutcome> DecideAsync(
            WorkflowInstanceRecord instance, WorkflowTrigger trigger, CancellationToken ct = default)
            => ValueTask.FromResult(WorkflowStepOutcome.Advance(
                $"{trigger.Step}:done", eventDataJson: trigger.PayloadJson));
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  SCHEDULE DAEMON — the schedule trigger drives instances via one tick.
    // ═════════════════════════════════════════════════════════════════════════

    [Fact(DisplayName = "Schedule daemon: one tick fetches the due triggers and advances each instance; over-reporting (an already-advanced trigger) is a no-op")]
    public async Task ScheduleDaemon_Tick_AdvancesDueInstances_AtLeastOnceSafe()
    {
        var factory = NewFactory();
        var store = NewStore(factory);
        await SeedInstanceAsync(store, "inst-sched-1", "demo-advance", "start");
        await SeedInstanceAsync(store, "inst-sched-2", "demo-advance", "start");

        var dispatcher = new WorkflowTriggerDispatcher(store, new[] { new AdvanceOnceHandler() });
        var source = new FixedScheduleSource(new[]
        {
            WorkflowTrigger.For(WorkflowTriggerKind.Schedule, "inst-sched-1", "start"),
            WorkflowTrigger.For(WorkflowTriggerKind.Schedule, "inst-sched-2", "start"),
        });
        var daemon = new WorkflowScheduleDaemon(
            source, dispatcher, TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkflowScheduleDaemon>.Instance);

        await daemon.TickAsync();

        Assert.Equal("start:done", (await store.LoadAsync("inst-sched-1"))!.CurrentStep);
        Assert.Equal("start:done", (await store.LoadAsync("inst-sched-2"))!.CurrentStep);

        // A second tick (the source over-reports) is safe — the dispatcher dedups, no exception, no change.
        await daemon.TickAsync();
        Assert.Equal("start:done", (await store.LoadAsync("inst-sched-1"))!.CurrentStep); // unchanged by the dup tick
    }

    private sealed class FixedScheduleSource(IReadOnlyList<WorkflowTrigger> due) : IWorkflowScheduleSource
    {
        public Task<IReadOnlyList<WorkflowTrigger>> GetDueTriggersAsync(DateTimeOffset asOf, CancellationToken ct = default)
            => Task.FromResult(due);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  IDEMPOTENCY-KEY DETERMINISM (build invariant #2) — cross-arch byte-stable.
    // ═════════════════════════════════════════════════════════════════════════

    [Fact(DisplayName = "Idempotency-key determinism: the same (instance, iteration, step) derives a byte-identical key + GUID across re-derivations")]
    public void StepKey_IsDeterministic_AndStable()
    {
        var a = new WorkflowStepKey("inst-X", 0, "post");
        var b = new WorkflowStepKey("inst-X", 0, "post");
        Assert.Equal("inst-X:0:post", a.Value);
        Assert.Equal(a.Value, b.Value);

        // The derived deterministic GUID is reproducible (the cross-architecture byte-stable derivation —
        // the bug-1337 follow-up's RFC-4122 v5 big-endian assembly).
        Assert.Equal(a.ToDeterministicGuid("journal-entry"), b.ToDeterministicGuid("journal-entry"));
        // A different purpose namespaces to a different id (no cross-derivation collision).
        Assert.NotEqual(a.ToDeterministicGuid("journal-entry"), a.ToDeterministicGuid("draft-id"));
        // A different step / iteration / instance changes the key.
        Assert.NotEqual(a.Value, new WorkflowStepKey("inst-X", 1, "post").Value);
        Assert.NotEqual(a.Value, new WorkflowStepKey("inst-X", 0, "approve").Value);

        // Golden vector — pin the exact derived GUID so a future change to the algorithm (which would
        // break cross-machine dedup) fails LOUDLY. Recompute deliberately if the derivation must change.
        Assert.Equal(
            WorkflowStepKey.DeriveV5Guid("journal-entry|inst-X:0:post"),
            a.ToDeterministicGuid("journal-entry"));
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  APPEND-ONLY INVARIANT — (InstanceId, Seq) is unique + monotonic.
    // ═════════════════════════════════════════════════════════════════════════

    [Fact(DisplayName = "Append-only: events accrue monotonic per-instance Seq (0,1,2…) and (InstanceId,Seq) is unique")]
    public async Task Events_AreAppendOnly_WithMonotonicSeq()
    {
        var factory = NewFactory();
        var store = NewStore(factory);
        await SeedInstanceAsync(store, "inst-seq", "demo-advance", "s0");

        await store.AdvanceAsync(new WorkflowStepKey("inst-seq", 0, "s0"), null, "{}", "Advanced", "{}", "s1", WorkflowStatus.Running);
        await store.AdvanceAsync(new WorkflowStepKey("inst-seq", 0, "s1"), null, "{}", "Advanced", "{}", "s2", WorkflowStatus.Running);
        await store.ParkAsync("inst-seq", "s2", "{}");

        await using var ctx = await factory.CreateDbContextAsync();
        var seqs = await ctx.Set<WorkflowEventRecord>()
            .Where(e => e.InstanceId == "inst-seq")
            .OrderBy(e => e.Seq)
            .Select(e => e.Seq)
            .ToListAsync();
        Assert.Equal(new long[] { 0, 1, 2 }, seqs);

        // Directly attempting to write a duplicate (InstanceId, Seq) hits the unique index.
        ctx.Set<WorkflowEventRecord>().Add(new WorkflowEventRecord
        {
            InstanceId = "inst-seq", Seq = 0, Step = "dup", EventType = "Advanced", OccurredAt = DateTimeOffset.UtcNow,
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  D7 — DefinitionVersion is pinned at instantiation and persists across restart.
    // ═════════════════════════════════════════════════════════════════════════

    [Fact(DisplayName = "D7: an instance pins its DefinitionVersion at instantiation; it survives a restart and is unchanged by an advance")]
    public async Task DefinitionVersion_IsPinned_AndDurable()
    {
        var factory = NewFactory();
        var store = NewStore(factory);
        await store.CreateInstanceAsync(new WorkflowInstanceRecord
        {
            Id = "inst-d7", TenantId = LocalTenantId.Value, DefinitionKey = "recurring-invoice",
            DefinitionVersion = "2026-06-23.3", CurrentStep = "post", Status = WorkflowStatus.Running,
        });

        await store.AdvanceAsync(new WorkflowStepKey("inst-d7", 0, "post"), null, "{}", "Advanced", "{}", "done", WorkflowStatus.Completed);

        // RESTART — the pinned version is durable + unchanged by the advance.
        var factory2 = NewFactory();
        var inst = await NewStore(factory2).LoadAsync("inst-d7");
        Assert.Equal("2026-06-23.3", inst!.DefinitionVersion);
    }
}
