using System.Globalization;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Blocks.FinancialAr.Models;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Blocks.FinancialAr.Data;
using Harborline.Api.Blocks.FinancialLedger.Data;
using Harborline.Api.Foundation.Assets.Common;
// PartyId lives in People.Foundation.Models (used by the seeded schedule).
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.Foundation.Scheduling;
using Harborline.Api.Foundation.Scheduling.DependencyInjection;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Workflow;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Workflow;

/// <summary>
/// ADR 0135 SLICE 3 — the END-TO-END instantiation surface: a real invoice-issued event INSTANTIATES an
/// <c>invoice-approval</c> Process and drives it through the engine; real <see cref="RecurringInvoiceSchedule"/>
/// rows INSTANTIATE <c>recurring-generation</c> Processes the daemon then drives. This is the goal gate —
/// nothing in slices 1/2 actually CREATED a Process from a domain event/row; this proves it now does, durably,
/// against the REAL recoverable <see cref="LocalNodeDbContext"/> with the REAL host financial effects (a
/// balanced JE co-committed with the advance).
/// </summary>
public sealed class NodeWorkflowInstantiationTests : IAsyncLifetime
{
    private string _dir = null!;
    private string _dbPath = null!;
    private static readonly TenantId LocalTenantId = new("local");

    public async Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "adr0135-slice3-" + Guid.NewGuid().ToString("N"));
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
        // Financial-ledger (JE) + AR (RecurringInvoiceSchedule) + workflow modules — the schema the
        // instantiation surface + the handler effects touch.
        services.AddSingleton<IHarborlineEntityModule, FinancialLedgerEntityModule>();
        services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialPeriods.Data.FinancialPeriodsEntityModule>();
        services.AddSingleton<IHarborlineEntityModule, ArEntityModule>();
        services.AddSingleton<IHarborlineEntityModule, WorkflowEntityModule>();
        services.AddDbContextFactory<LocalNodeDbContext>(o => o.UseSqlite($"Data Source={_dbPath};Pooling=False"));
        return services.BuildServiceProvider().GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
    }

    private NodeEfWorkflowStore NewStore(IDbContextFactory<LocalNodeDbContext> factory)
        => new(factory);

    private static NodeWorkflowInstantiationService NewInstantiation(
        IWorkflowStore store, IDbContextFactory<LocalNodeDbContext> factory)
        => new(store, factory);

    private static IWorkflowStepHandler InvoiceHandler()
        => new InvoiceApprovalHandler(
            NodeWorkflowDefinitions.InvoiceApprovalThresholdTable(),
            new NodeInvoiceApprovalContext());

    private static IWorkflowStepHandler RecurringHandler()
        => new RecurringGenerationHandler(new NodeRecurringGenerationContext());

    private async Task<int> CountJournalEntriesAsync(IDbContextFactory<LocalNodeDbContext> factory)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        return await ctx.Set<JournalEntry>().CountAsync(j => j.TenantId == LocalTenantId);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  invoice-approval instantiation (the invoice-issued event path)
    // ═════════════════════════════════════════════════════════════════════════

    [Fact(DisplayName = "E2E invoice-issued (UNDER $5k): instantiate the approval Process for an invoice → drive decide → engine AUTO-POSTS exactly one JE (no human)")]
    public async Task InvoiceIssued_UnderThreshold_InstantiatesAndAutoPosts()
    {
        var factory = NewFactory();
        var store = NewStore(factory);
        var instantiation = NewInstantiation(store, factory);
        var dispatcher = new WorkflowTriggerDispatcher(store, new[] { InvoiceHandler() });

        // The invoice-issued event instantiates the approval Process (D7 pinned at instantiation).
        var instanceId = await instantiation.StartInvoiceApprovalAsync(
            LocalTenantId, invoiceId: "INV-UNDER", amount: 1000m,
            debitAccount: "1100", creditAccount: "4000", memo: "under-threshold issue");

        var pinned = await store.LoadAsync(instanceId);
        Assert.Equal(NodeWorkflowDefinitions.InvoiceApprovalV1Version, pinned!.DefinitionVersion);

        // Drive the decide trigger → under threshold → engine auto-posts.
        var result = await dispatcher.DispatchAsync(
            WorkflowTrigger.For(WorkflowTriggerKind.Event, instanceId, InvoiceApprovalSteps.Decide));

        Assert.Equal(WorkflowDispatchResult.Advanced, result);
        Assert.Equal(1, await CountJournalEntriesAsync(factory));
        Assert.Equal(WorkflowStatus.Completed, (await store.LoadAsync(instanceId))!.Status);
    }

    [Fact(DisplayName = "E2E invoice-issued (OVER $5k): instantiate → decide PARKS (no JE) → approve posts EXACTLY ONE → a redelivered approve is a no-op; reject on a separate invoice posts NONE")]
    public async Task InvoiceIssued_OverThreshold_ParksApprovesOnce_AndRejectPostsNone()
    {
        var factory = NewFactory();
        var store = NewStore(factory);
        var instantiation = NewInstantiation(store, factory);
        var dispatcher = new WorkflowTriggerDispatcher(store, new[] { InvoiceHandler() });

        // OVER threshold → instantiate + decide → PARK on the approve human-task. NO JE yet.
        var overId = await instantiation.StartInvoiceApprovalAsync(
            LocalTenantId, "INV-OVER", 7500m, "1100", "4000", "over-threshold issue");
        Assert.Equal(WorkflowDispatchResult.Parked,
            await dispatcher.DispatchAsync(WorkflowTrigger.For(WorkflowTriggerKind.Event, overId, InvoiceApprovalSteps.Decide)));
        Assert.Equal(0, await CountJournalEntriesAsync(factory));

        // Approve → advance to the CP post with the effect → EXACTLY ONE JE.
        Assert.Equal(WorkflowDispatchResult.Advanced,
            await dispatcher.DispatchAsync(WorkflowTrigger.For(
                WorkflowTriggerKind.HumanAction, overId, InvoiceApprovalSteps.Approve, "{\"decision\":\"approve\"}")));
        Assert.Equal(1, await CountJournalEntriesAsync(factory));

        // A redelivered approve is a no-op (instance is Completed) — still EXACTLY ONE JE.
        Assert.Equal(WorkflowDispatchResult.Terminal,
            await dispatcher.DispatchAsync(WorkflowTrigger.For(
                WorkflowTriggerKind.HumanAction, overId, InvoiceApprovalSteps.Approve, "{\"decision\":\"approve\"}")));
        Assert.Equal(1, await CountJournalEntriesAsync(factory));

        // A SEPARATE over-threshold invoice that is REJECTED posts NO JE → total stays at 1.
        var rejId = await instantiation.StartInvoiceApprovalAsync(
            LocalTenantId, "INV-REJECT", 9000m, "1100", "4000", "reject me");
        await dispatcher.DispatchAsync(WorkflowTrigger.For(WorkflowTriggerKind.Event, rejId, InvoiceApprovalSteps.Decide));
        Assert.Equal(WorkflowDispatchResult.Advanced,
            await dispatcher.DispatchAsync(WorkflowTrigger.For(
                WorkflowTriggerKind.HumanAction, rejId, InvoiceApprovalSteps.Approve, "{\"decision\":\"reject\"}")));
        Assert.Equal(1, await CountJournalEntriesAsync(factory));   // no post on reject
        Assert.Equal(InvoiceApprovalSteps.Rejected, (await store.LoadAsync(rejId))!.CurrentStep);
    }

    [Fact(DisplayName = "E2E invoice-approval instantiation is idempotent: re-instantiating the SAME invoice's approval returns the same instance, no duplicate Process")]
    public async Task InvoiceApprovalInstantiation_IsIdempotent()
    {
        var factory = NewFactory();
        var store = NewStore(factory);
        var instantiation = NewInstantiation(store, factory);

        var first = await instantiation.StartInvoiceApprovalAsync(LocalTenantId, "INV-DUP", 1000m, "1100", "4000", "m");
        var second = await instantiation.StartInvoiceApprovalAsync(LocalTenantId, "INV-DUP", 1000m, "1100", "4000", "m");
        Assert.Equal(first, second);

        await using var ctx = await factory.CreateDbContextAsync();
        var count = await ctx.Set<WorkflowInstanceRecord>().CountAsync(i => i.Id == first);
        Assert.Equal(1, count);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  recurring-generation instantiation (from RecurringInvoiceSchedule rows) → daemon drives it
    // ═════════════════════════════════════════════════════════════════════════

    private async Task SeedScheduleAsync(
        IDbContextFactory<LocalNodeDbContext> factory, string scheduleId, string rrule, DateOnly startsOn, decimal unitPrice)
    {
        var schedule = RecurringInvoiceSchedule.Create(
            tenantId: LocalTenantId,
            chartId: new ChartOfAccountsId("chart-1"),
            customerId: new PartyId("cust-1"),
            arAccountId: new GLAccountId("1100"),
            recurrenceRule: rrule,
            startsOn: startsOn,
            timezone: "UTC",
            lineTemplates: new[]
            {
                new RecurringInvoiceLineTemplate("Monthly fee", 1m, unitPrice, new GLAccountId("4000")),
            },
            id: new RecurringInvoiceScheduleId(scheduleId));

        await using var ctx = await factory.CreateDbContextAsync();
        ctx.Set<RecurringInvoiceSchedule>().Add(schedule);
        await ctx.SaveChangesAsync();
    }

    [Fact(DisplayName = "E2E recurring: an Active RecurringInvoiceSchedule INSTANTIATES a recurring-generation Process → the daemon TICK drives it → a real occurrence JE posts (the engine is live off the schedule rows)")]
    public async Task RecurringSchedule_InstantiatesProcess_DaemonDrivesGeneration()
    {
        var factory = NewFactory();
        var store = NewStore(factory);
        var instantiation = NewInstantiation(store, factory);

        // A monthly schedule that STARTED in the past, so as-of-now there are due occurrences.
        await SeedScheduleAsync(factory, "SCHED-1", "FREQ=MONTHLY;BYMONTHDAY=1", new DateOnly(2026, 6, 1), 250m);

        // Instantiate the recurring-generation Process(es) from the schedule rows.
        var created = await instantiation.SyncRecurringGenerationProcessesAsync(LocalTenantId);
        Assert.Equal(1, created);

        // The PRODUCTION schedule source (RRULE over the recoverable instance state) + the daemon.
        var rruleServices = new ServiceCollection();
        rruleServices.AddFoundationScheduling();
        var rrule = rruleServices.BuildServiceProvider().GetRequiredService<IRruleExpansionService>();
        var source = new NodeRecurringScheduleSource(factory, rrule);
        var dispatcher = new WorkflowTriggerDispatcher(store, new[] { RecurringHandler() });

        var fixedClock = new FixedClock(new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero));
        var daemon = new WorkflowScheduleDaemon(source, dispatcher, fixedClock, NullLogger<WorkflowScheduleDaemon>.Instance);

        var before = await CountJournalEntriesAsync(factory);
        await daemon.TickAsync();
        var after = await CountJournalEntriesAsync(factory);
        Assert.True(after > before, "the instantiated recurring Process should be advanced by the daemon and post at least one occurrence JE");

        // A second tick is a no-op (the source over-reports the SAME due occurrence; the dispatcher dedups).
        await daemon.TickAsync();
        Assert.Equal(after, await CountJournalEntriesAsync(factory));
    }

    [Fact(DisplayName = "E2E recurring CRASH-RESUME: after the daemon generates, a RESTART (fresh store over the same db) + re-tick re-derives the IDENTICAL occurrence → NO double-post")]
    public async Task RecurringGeneration_CrashResume_NoDoublePost()
    {
        var factory = NewFactory();
        var store = NewStore(factory);
        var instantiation = NewInstantiation(store, factory);
        await SeedScheduleAsync(factory, "SCHED-CR", "FREQ=MONTHLY;BYMONTHDAY=1", new DateOnly(2026, 6, 1), 250m);
        await instantiation.SyncRecurringGenerationProcessesAsync(LocalTenantId);

        var rruleServices = new ServiceCollection();
        rruleServices.AddFoundationScheduling();
        var rrule = rruleServices.BuildServiceProvider().GetRequiredService<IRruleExpansionService>();
        var fixedClock = new FixedClock(new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero));

        // First daemon (first "process boot").
        var daemon1 = new WorkflowScheduleDaemon(
            new NodeRecurringScheduleSource(factory, rrule),
            new WorkflowTriggerDispatcher(store, new[] { RecurringHandler() }),
            fixedClock, NullLogger<WorkflowScheduleDaemon>.Instance);
        await daemon1.TickAsync();
        var afterFirstBoot = await CountJournalEntriesAsync(factory);
        Assert.True(afterFirstBoot > 0);

        // RESTART — a fresh store + dispatcher + daemon over the SAME db (simulating a crash + resume). The
        // deterministic occurrence id + the per-occurrence idempotency key make the re-tick a no-op.
        var factory2 = NewFactory();
        var store2 = NewStore(factory2);
        var daemon2 = new WorkflowScheduleDaemon(
            new NodeRecurringScheduleSource(factory2, rrule),
            new WorkflowTriggerDispatcher(store2, new[] { RecurringHandler() }),
            fixedClock, NullLogger<WorkflowScheduleDaemon>.Instance);
        await daemon2.TickAsync();

        Assert.Equal(afterFirstBoot, await CountJournalEntriesAsync(factory2));   // NO double-post across the resume
    }

    [Fact(DisplayName = "E2E recurring instantiation is idempotent: a second sync creates ZERO new Processes for already-instantiated schedules")]
    public async Task RecurringInstantiation_IsIdempotent()
    {
        var factory = NewFactory();
        var store = NewStore(factory);
        var instantiation = NewInstantiation(store, factory);
        await SeedScheduleAsync(factory, "SCHED-IDEM", "FREQ=MONTHLY;BYMONTHDAY=1", new DateOnly(2026, 6, 1), 100m);

        Assert.Equal(1, await instantiation.SyncRecurringGenerationProcessesAsync(LocalTenantId));
        Assert.Equal(0, await instantiation.SyncRecurringGenerationProcessesAsync(LocalTenantId));   // already exists
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
