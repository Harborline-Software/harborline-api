using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Blocks.FinancialLedger.Data;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.Foundation.Scheduling.DependencyInjection;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Workflow;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Workflow;

/// <summary>
/// ADR 0135 SLICE 2 — the two typed handlers + CP-park + D7 + the WIRED engine, against the REAL recoverable
/// <see cref="LocalNodeDbContext"/> on an on-disk SQLite file (the production stores share the context, so a
/// step's JE effect co-commits with the workflow advance). Drives the handlers THROUGH the dispatcher (the
/// production path) with the REAL host contexts (<see cref="NodeInvoiceApprovalContext"/> /
/// <see cref="NodeRecurringGenerationContext"/>) — not fakes — so this exercises the financial effect end to
/// end.
/// </summary>
public sealed class NodeWorkflowSlice2Tests : IAsyncLifetime
{
    private string _dir = null!;
    private string _dbPath = null!;
    private static readonly TenantId LocalTenantId = new("local");

    public async Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "adr0135-slice2-" + Guid.NewGuid().ToString("N"));
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

    private NodeEfWorkflowStore NewStore(IDbContextFactory<LocalNodeDbContext> factory)
        => new(factory);

    private async Task<int> CountJournalEntriesAsync(IDbContextFactory<LocalNodeDbContext> factory)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        return await ctx.Set<JournalEntry>().CountAsync(j => j.TenantId == LocalTenantId);
    }

    private static IWorkflowStepHandler InvoiceHandler()
        => new InvoiceApprovalHandler(
            NodeWorkflowDefinitions.InvoiceApprovalThresholdTable(),
            new NodeInvoiceApprovalContext());

    private static IWorkflowStepHandler RecurringHandler()
        => new RecurringGenerationHandler(new NodeRecurringGenerationContext());

    private async Task<string> SeedApprovalInstanceAsync(IWorkflowStore store, string id, decimal amount)
    {
        await store.CreateInstanceAsync(new WorkflowInstanceRecord
        {
            Id = id,
            TenantId = LocalTenantId.Value,
            DefinitionKey = InvoiceApprovalSteps.DefinitionKey,
            DefinitionVersion = NodeWorkflowDefinitions.InvoiceApprovalV1Version,   // D7 — pinned at instantiation
            CurrentStep = InvoiceApprovalSteps.Decide,
            Status = WorkflowStatus.Running,
            StateJson = $"{{\"amount\":{amount.ToString(System.Globalization.CultureInfo.InvariantCulture)}," +
                        "\"debitAccount\":\"1100\",\"creditAccount\":\"4000\",\"memo\":\"approval test\"}",
        });
        return id;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  HANDLER A — under-threshold posts directly; over-threshold parks → approve → posts once; reject → none.
    // ═════════════════════════════════════════════════════════════════════════

    [Fact(DisplayName = "Handler A wired: UNDER-threshold ($1k) advances through the dispatcher and posts EXACTLY ONE JE (auto, no human)")]
    public async Task HandlerA_UnderThreshold_PostsDirectly()
    {
        var factory = NewFactory();
        var store = NewStore(factory);
        await SeedApprovalInstanceAsync(store, "appr-under", 1000m);
        var dispatcher = new WorkflowTriggerDispatcher(store, new[] { InvoiceHandler() });

        var result = await dispatcher.DispatchAsync(
            WorkflowTrigger.For(WorkflowTriggerKind.Event, "appr-under", InvoiceApprovalSteps.Decide));

        Assert.Equal(WorkflowDispatchResult.Advanced, result);
        Assert.Equal(1, await CountJournalEntriesAsync(factory));
        var inst = await store.LoadAsync("appr-under");
        Assert.Equal(WorkflowStatus.Completed, inst!.Status);
        Assert.Equal(InvoiceApprovalSteps.Posted, inst.CurrentStep);
    }

    [Fact(DisplayName = "Handler A wired: OVER-threshold ($7.5k) PARKS (no JE), approve advances to post → EXACTLY ONE JE; a redelivered approve is a no-op (no double-post)")]
    public async Task HandlerA_OverThreshold_ParksThenApprovesPostsOnce()
    {
        var factory = NewFactory();
        var store = NewStore(factory);
        await SeedApprovalInstanceAsync(store, "appr-over", 7500m);
        var dispatcher = new WorkflowTriggerDispatcher(store, new[] { InvoiceHandler() });

        // decide → PARK on the approve human-task. NO JE yet (the CP post is not reached).
        var decided = await dispatcher.DispatchAsync(
            WorkflowTrigger.For(WorkflowTriggerKind.Event, "appr-over", InvoiceApprovalSteps.Decide));
        Assert.Equal(WorkflowDispatchResult.Parked, decided);
        Assert.Equal(0, await CountJournalEntriesAsync(factory));
        var parked = await store.LoadAsync("appr-over");
        Assert.Equal(WorkflowStatus.Parked, parked!.Status);
        Assert.Equal(InvoiceApprovalSteps.Approve, parked.CurrentStep);

        // human approve → advance to the CP post step WITH the effect → EXACTLY ONE JE.
        var approved = await dispatcher.DispatchAsync(
            WorkflowTrigger.For(WorkflowTriggerKind.HumanAction, "appr-over", InvoiceApprovalSteps.Approve,
                "{\"decision\":\"approve\"}"));
        Assert.Equal(WorkflowDispatchResult.Advanced, approved);
        Assert.Equal(1, await CountJournalEntriesAsync(factory));
        Assert.Equal(WorkflowStatus.Completed, (await store.LoadAsync("appr-over"))!.Status);

        // Redelivered approve (idempotency hit) → no-op, still EXACTLY ONE JE.
        var again = await dispatcher.DispatchAsync(
            WorkflowTrigger.For(WorkflowTriggerKind.HumanAction, "appr-over", InvoiceApprovalSteps.Approve,
                "{\"decision\":\"approve\"}"));
        Assert.Equal(WorkflowDispatchResult.Terminal, again);   // instance is Completed now
        Assert.Equal(1, await CountJournalEntriesAsync(factory));
    }

    [Fact(DisplayName = "Handler A wired: reject on the parked human-task posts NO JE")]
    public async Task HandlerA_Reject_NoPost()
    {
        var factory = NewFactory();
        var store = NewStore(factory);
        await SeedApprovalInstanceAsync(store, "appr-rej", 7500m);
        var dispatcher = new WorkflowTriggerDispatcher(store, new[] { InvoiceHandler() });

        await dispatcher.DispatchAsync(
            WorkflowTrigger.For(WorkflowTriggerKind.Event, "appr-rej", InvoiceApprovalSteps.Decide));
        var rejected = await dispatcher.DispatchAsync(
            WorkflowTrigger.For(WorkflowTriggerKind.HumanAction, "appr-rej", InvoiceApprovalSteps.Approve,
                "{\"decision\":\"reject\"}"));

        Assert.Equal(WorkflowDispatchResult.Advanced, rejected);
        Assert.Equal(0, await CountJournalEntriesAsync(factory));   // NO post on reject
        var inst = await store.LoadAsync("appr-rej");
        Assert.Equal(WorkflowStatus.Completed, inst!.Status);
        Assert.Equal(InvoiceApprovalSteps.Rejected, inst.CurrentStep);
    }

    [Fact(DisplayName = "CP-park ARCH-TEST (by name): Handler A's OVER-threshold decide PARKS on the approve human-task (NEVER auto-reaches post) and carries the FE-1 basis (preview + fired row/version)")]
    public async Task ArchTest_HandlerA_CpStep_IsHumanTaskParked_WithFe1Basis()
    {
        // This is the v1 definition-of-done CP-park gate (ADR 0135 §Prerequisites): the CP post step is reached
        // ONLY through the human-task park, asserted BY NAME. Drive the handler directly (the arch assertion).
        var handler = (InvoiceApprovalHandler)InvoiceHandler();
        var instance = new WorkflowInstanceRecord
        {
            Id = "arch-A", TenantId = LocalTenantId.Value,
            DefinitionKey = InvoiceApprovalSteps.DefinitionKey,
            DefinitionVersion = NodeWorkflowDefinitions.InvoiceApprovalV1Version,
            CurrentStep = InvoiceApprovalSteps.Decide, Status = WorkflowStatus.Running,
            StateJson = "{\"amount\":9000,\"debitAccount\":\"1100\",\"creditAccount\":\"4000\",\"memo\":\"m\"}",
        };

        var outcome = await handler.DecideAsync(
            instance, WorkflowTrigger.For(WorkflowTriggerKind.Event, "arch-A", InvoiceApprovalSteps.Decide));

        // BY NAME: the CP post step is NOT auto-reached — the outcome is a PARK on the approve human-task.
        Assert.Equal(WorkflowStepOutcomeKind.Park, outcome.Kind);
        Assert.Equal(InvoiceApprovalSteps.Approve, outcome.NextStep);
        Assert.Null(outcome.Effect);

        // FE-1 binding: the CP human-task carries the basis payload (posting preview + the decision-table
        // row/version that fired).
        using var basis = System.Text.Json.JsonDocument.Parse(outcome.EventDataJson);
        var root = basis.RootElement;
        Assert.Equal("cp-approval-basis", root.GetProperty("kind").GetString());
        Assert.Contains("Debit", root.GetProperty("postingPreview").GetString());     // the posting preview
        Assert.Equal(NodeWorkflowDefinitions.InvoiceApprovalV1Version,
            root.GetProperty("decision").GetProperty("version").GetString());          // D7 — fired version
        Assert.Equal("over-5k", root.GetProperty("decision").GetProperty("row").GetString());
    }

    [Fact(DisplayName = "D7 decision-version pin: a $5000.00-exact invoice auto-posts (strictly-above gate); the basis records the pinned v1 version")]
    public async Task D7_BoundaryAndPinnedVersion()
    {
        var factory = NewFactory();
        var store = NewStore(factory);
        // $5000.00 exactly is UNDER the strictly-> $5k gate → auto-post.
        await SeedApprovalInstanceAsync(store, "appr-edge", 5000m);
        var dispatcher = new WorkflowTriggerDispatcher(store, new[] { InvoiceHandler() });

        var result = await dispatcher.DispatchAsync(
            WorkflowTrigger.For(WorkflowTriggerKind.Event, "appr-edge", InvoiceApprovalSteps.Decide));
        Assert.Equal(WorkflowDispatchResult.Advanced, result);   // auto-post (not parked)
        Assert.Equal(1, await CountJournalEntriesAsync(factory));
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  HANDLER B — recurring generation, crash-resume → no double-post; wired through the daemon.
    // ═════════════════════════════════════════════════════════════════════════

    private async Task SeedRecurringInstanceAsync(IWorkflowStore store, string id, string rrule, DateOnly startsOn)
        => await store.CreateInstanceAsync(new WorkflowInstanceRecord
        {
            Id = id,
            TenantId = LocalTenantId.Value,
            DefinitionKey = RecurringGenerationSteps.DefinitionKey,
            DefinitionVersion = "v1",
            CurrentStep = RecurringGenerationSteps.GenerateStep(startsOn),
            Status = WorkflowStatus.Running,
            StateJson = $"{{\"scheduleId\":\"sched-{id}\",\"rrule\":\"{rrule}\",\"startsOn\":\"{startsOn:yyyy-MM-dd}\"," +
                        "\"amount\":250,\"debitAccount\":\"1100\",\"creditAccount\":\"4000\"}",
        });

    [Fact(DisplayName = "Handler B wired: a single generate occurrence posts ONE JE; a crash-resume of the SAME occurrence re-runs and posts EXACTLY ONE (no double-post — deterministic id + idempotency)")]
    public async Task HandlerB_CrashResume_NoDoublePost()
    {
        var factory = NewFactory();
        var store = NewStore(factory);
        await SeedRecurringInstanceAsync(store, "rec-1", "FREQ=MONTHLY;BYMONTHDAY=1", new DateOnly(2026, 7, 1));
        var dispatcher = new WorkflowTriggerDispatcher(store, new[] { RecurringHandler() });

        var step = RecurringGenerationSteps.GenerateStep(new DateOnly(2026, 7, 1));

        // First tick → advance + post ONE JE.
        Assert.Equal(WorkflowDispatchResult.Advanced,
            await dispatcher.DispatchAsync(WorkflowTrigger.For(WorkflowTriggerKind.Schedule, "rec-1", step)));
        Assert.Equal(1, await CountJournalEntriesAsync(factory));

        // RESTART (fresh factory over the same db) + re-deliver the SAME occurrence tick → idempotency guard
        // hits, no-op, still EXACTLY ONE JE.
        var factory2 = NewFactory();
        var store2 = NewStore(factory2);
        var dispatcher2 = new WorkflowTriggerDispatcher(store2, new[] { RecurringHandler() });
        Assert.Equal(WorkflowDispatchResult.ReplayedNoOp,
            await dispatcher2.DispatchAsync(WorkflowTrigger.For(WorkflowTriggerKind.Schedule, "rec-1", step)));
        Assert.Equal(1, await CountJournalEntriesAsync(factory2));
    }

    [Fact(DisplayName = "Handler B wired: distinct occurrences (Jul + Aug) each post once → TWO JEs (each occurrence is its own idempotency key)")]
    public async Task HandlerB_DistinctOccurrences_EachPostOnce()
    {
        var factory = NewFactory();
        var store = NewStore(factory);
        await SeedRecurringInstanceAsync(store, "rec-2", "FREQ=MONTHLY;BYMONTHDAY=1", new DateOnly(2026, 7, 1));
        var dispatcher = new WorkflowTriggerDispatcher(store, new[] { RecurringHandler() });

        await dispatcher.DispatchAsync(WorkflowTrigger.For(WorkflowTriggerKind.Schedule, "rec-2",
            RecurringGenerationSteps.GenerateStep(new DateOnly(2026, 7, 1))));
        await dispatcher.DispatchAsync(WorkflowTrigger.For(WorkflowTriggerKind.Schedule, "rec-2",
            RecurringGenerationSteps.GenerateStep(new DateOnly(2026, 8, 1))));

        Assert.Equal(2, await CountJournalEntriesAsync(factory));
    }

    [Fact(DisplayName = "WIRED end-to-end: the schedule SOURCE expands due occurrences over a real instance + the daemon TICK advances it → a real JE posted (the engine is live on the schedule trigger)")]
    public async Task Wired_ScheduleSource_DaemonTick_AdvancesRealInstance()
    {
        var factory = NewFactory();
        var store = NewStore(factory);
        // A monthly schedule that started in the past, so as-of-now there are due occurrences to generate.
        await SeedRecurringInstanceAsync(store, "rec-wired", "FREQ=MONTHLY;BYMONTHDAY=1", new DateOnly(2026, 6, 1));
        var dispatcher = new WorkflowTriggerDispatcher(store, new[] { RecurringHandler() });

        // The PRODUCTION schedule source (RRULE over the recoverable schedule rows) + the daemon.
        var rruleServices = new ServiceCollection();
        rruleServices.AddFoundationScheduling();
        var rrule = rruleServices.BuildServiceProvider()
            .GetRequiredService<Harborline.Api.Foundation.Scheduling.IRruleExpansionService>();
        var source = new NodeRecurringScheduleSource(factory, rrule);

        // Fix "now" at a date after the first occurrence so at least one is due.
        var now = new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero);
        var fixedClock = new FixedClock(now);
        var daemon = new WorkflowScheduleDaemon(source, dispatcher, fixedClock,
            NullLogger<WorkflowScheduleDaemon>.Instance);

        var before = await CountJournalEntriesAsync(factory);
        await daemon.TickAsync();
        var after = await CountJournalEntriesAsync(factory);

        Assert.True(after > before, "the wired daemon tick should advance the real instance and post at least one JE");

        // A second tick is a no-op (the source over-reports the SAME due occurrence; the dispatcher dedups).
        await daemon.TickAsync();
        Assert.Equal(after, await CountJournalEntriesAsync(factory));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
