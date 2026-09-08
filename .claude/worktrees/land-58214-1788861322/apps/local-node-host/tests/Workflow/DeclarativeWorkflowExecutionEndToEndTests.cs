using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialLedger.Services;
using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Blocks.Workflow.Interpreter;
using Harborline.Api.Blocks.FinancialLedger.Data;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Workflow;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Workflow;

/// <summary>
/// ADR 0135 A1 (W-8) — the SUBSTRATE end-to-end proof of the general declarative interpreter, against the REAL
/// recoverable <see cref="LocalNodeDbContext"/> (on-disk SQLite), the REAL broker-PEP + effect factory, and the
/// REAL admission gate. This is the ledger's "Owed #6" substrate-layer executed run: the A3 three-way-match
/// workflow is authored → admitted → persisted → published → instantiated → EXECUTED through the interpreter,
/// and a human confirm posts a real balanced JE THROUGH the broker (a Harborline App browser screenshot of the same
/// flow is the separable PR (c) evidence a live web build produces).
/// <list type="number">
///   <item>seed the three-way-match definition (fail-closed admission at persist) + instantiate a Process;</item>
///   <item>dispatch <c>matched</c> → the interpreter walks match → PARKS on the human approval, <b>no JE</b>;</item>
///   <item>dispatch a human <c>approve</c> → the CP post is built ONLY via the SoD-gated broker → <b>EXACTLY
///     ONE balanced JournalEntry</b> posts + an audited advance, instance Completed;</item>
///   <item>negative: a second Process <c>reject</c>ed → <b>no JE</b>, and an override is recorded.</item>
/// </list>
/// </summary>
public sealed class DeclarativeWorkflowExecutionEndToEndTests : IAsyncLifetime
{
    private string _dir = null!;
    private string _dbPath = null!;
    private static readonly TenantId LocalTenant = new("local");
    private static readonly Guid HumanConfirmer = Guid.Parse("40000000-0000-0000-0000-0000000000c1");

    public async Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "adr0135-a1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "local-node.db");
        await using var ctx = await NewProvider().GetRequiredService<IDbContextFactory<LocalNodeDbContext>>()
            .CreateDbContextAsync();
        await ctx.Database.EnsureCreatedAsync();
    }

    public Task DisposeAsync()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort */ }
        return Task.CompletedTask;
    }

    [Fact(DisplayName = "W-8: authored three-way-match executes — matched PARKS (no JE), human approve posts EXACTLY ONE balanced JE through the broker")]
    public async Task Authored_workflow_executes_park_then_human_confirm_posts_one_je()
    {
        var sp = NewProvider();
        var defStore = sp.GetRequiredService<IWorkflowDefinitionStore>();
        var workflowStore = sp.GetRequiredService<IWorkflowStore>();
        var dispatcher = sp.GetRequiredService<IWorkflowTriggerDispatcher>();

        // 1. Author → admit → persist → publish (the fail-closed admission runs at persist), then instantiate.
        await PublishDefinitionSeedAsync(defStore);
        var instanceId = await NodeThreeWayMatchWorkflowSeed.InstantiateAsync(
            workflowStore, LocalTenant, billId: "bill-100", amount: 4200m,
            expenseAccount: "6000", payableAccount: "2000", memo: "Vendor bill (3-way matched)");

        // 2. matched → the interpreter walks matching → review and PARKS on the human approval. No JE yet.
        var parked = await dispatcher.DispatchAsync(
            WorkflowTrigger.For(WorkflowTriggerKind.Event, instanceId, NodeThreeWayMatchWorkflowSeed.MatchingState),
            TestAuthorization.AllowedDecision(LocalTenant, instanceId));
        Assert.Equal(WorkflowDispatchResult.Parked, parked);

        var afterPark = await workflowStore.LoadAsync(instanceId);
        Assert.Equal(WorkflowStatus.Parked, afterPark!.Status);
        Assert.Equal(NodeThreeWayMatchWorkflowSeed.ReviewState, afterPark.CurrentStep);
        Assert.Equal(0, await CountJournalEntriesAsync(sp));

        // 3. human approve → the CP post is built ONLY through the SoD-gated broker → exactly one balanced JE.
        var confirmed = await dispatcher.DispatchAsync(WorkflowTrigger.For(
            WorkflowTriggerKind.HumanAction, instanceId,
            NodeThreeWayMatchWorkflowSeed.ReviewState, "{\"decision\":\"approve\"}"),
            new WorkflowDispatchAuthority(
                TestAuthorization.AllowedDecision(LocalTenant, instanceId),
                TestAuthorization.AllowedDecision(
                    LocalTenant,
                    NodeLedgerPostingEffect.JournalEntryIdFor(afterPark).Value,
                    "journal-entry",
                    TeamRolePermissions.LedgerPost)));
        Assert.Equal(WorkflowDispatchResult.Advanced, confirmed);

        var afterPost = await workflowStore.LoadAsync(instanceId);
        Assert.Equal(WorkflowStatus.Completed, afterPost!.Status);
        Assert.Equal(NodeThreeWayMatchWorkflowSeed.PostedState, afterPost.CurrentStep);

        var jes = await JournalEntriesAsync(sp);
        Assert.Single(jes);
        var je = jes[0];
        // Balanced: Debit expense 6000 / Credit payable 2000, both 4200.
        Assert.Equal(4200m, je.Lines.Sum(l => l.Debit));
        Assert.Equal(4200m, je.Lines.Sum(l => l.Credit));
        Assert.Contains(je.Lines, l => l.AccountId == new GLAccountId("6000") && l.Debit == 4200m);
        Assert.Contains(je.Lines, l => l.AccountId == new GLAccountId("2000") && l.Credit == 4200m);
        Assert.Equal(JournalEntryStatus.Posted, je.Status);

        // The confirmation was recorded for the D-INV-7 override-rate metric.
        Assert.Equal(1, sp.GetRequiredService<CountingWorkflowApprovalDecisionSink>().ConfirmedCount);

        // A redelivered approve is a no-op (no double-post).
        var replay = await dispatcher.DispatchAsync(WorkflowTrigger.For(
            WorkflowTriggerKind.HumanAction, instanceId,
            NodeThreeWayMatchWorkflowSeed.ReviewState, "{\"decision\":\"approve\"}"),
            TestAuthorization.AllowedDecision(LocalTenant, instanceId));
        Assert.Equal(WorkflowDispatchResult.Terminal, replay); // instance already Completed
        Assert.Single(await JournalEntriesAsync(sp));
    }

    [Fact(DisplayName = "W-8 negative: a rejected three-way-match posts NO JE and records an override")]
    public async Task Rejected_workflow_posts_no_je_and_records_an_override()
    {
        var sp = NewProvider();
        var defStore = sp.GetRequiredService<IWorkflowDefinitionStore>();
        var workflowStore = sp.GetRequiredService<IWorkflowStore>();
        var dispatcher = sp.GetRequiredService<IWorkflowTriggerDispatcher>();

        await PublishDefinitionSeedAsync(defStore);
        var instanceId = await NodeThreeWayMatchWorkflowSeed.InstantiateAsync(
            workflowStore, LocalTenant, billId: "bill-200", amount: 999m,
            expenseAccount: "6000", payableAccount: "2000", memo: "Vendor bill (to reject)");

        await dispatcher.DispatchAsync(
            WorkflowTrigger.For(WorkflowTriggerKind.Event, instanceId, NodeThreeWayMatchWorkflowSeed.MatchingState),
            TestAuthorization.AllowedDecision(LocalTenant, instanceId));

        var rejected = await dispatcher.DispatchAsync(WorkflowTrigger.For(
            WorkflowTriggerKind.HumanAction, instanceId,
            NodeThreeWayMatchWorkflowSeed.ReviewState, "{\"decision\":\"reject\"}"),
            TestAuthorization.AllowedDecision(LocalTenant, instanceId));
        Assert.Equal(WorkflowDispatchResult.Advanced, rejected);

        var inst = await workflowStore.LoadAsync(instanceId);
        Assert.Equal(WorkflowStatus.Completed, inst!.Status);
        Assert.Equal("rejected", inst.CurrentStep);
        Assert.Equal(0, await CountJournalEntriesAsync(sp));
        Assert.Equal(1, sp.GetRequiredService<CountingWorkflowApprovalDecisionSink>().OverriddenCount);
    }

    [Fact]
    public async Task WorkflowEffect_CarriesAllowedDecision_AndRejectsMismatchedOrDeniedDecision()
    {
        var sp = NewProvider();
        var definitions = sp.GetRequiredService<IWorkflowDefinitionStore>();
        var workflows = sp.GetRequiredService<IWorkflowStore>();
        var dispatcher = sp.GetRequiredService<IWorkflowTriggerDispatcher>();
        await PublishDefinitionSeedAsync(definitions);
        var instanceId = await NodeThreeWayMatchWorkflowSeed.InstantiateAsync(
            workflows, LocalTenant, "pure-transition", 10m, "6000", "2000", "pure");

        var denied = await Harborline.Api.LocalNodeHost.Tests.Authorization.TestAuthorization.Gate(false).DecideAsync(
            Harborline.Api.LocalNodeHost.Tests.Authorization.TestAuthorization.Write(LocalTenant).Request(
                AuthorizationOperation.Parse(TeamRolePermissions.RecordsWrite), "record", instanceId));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => dispatcher.DispatchAsync(
            WorkflowTrigger.For(WorkflowTriggerKind.HumanAction, instanceId, NodeThreeWayMatchWorkflowSeed.MatchingState,
                "{\"decision\":\"reject\"}"), denied));
        Assert.Equal(NodeThreeWayMatchWorkflowSeed.MatchingState, (await workflows.LoadAsync(instanceId))!.CurrentStep);

        var forAnotherRecord = Harborline.Api.LocalNodeHost.Tests.Authorization.TestAuthorization.AllowedDecision(
            LocalTenant, instanceId + "-other");
        await Assert.ThrowsAsync<ArgumentException>(() => dispatcher.DispatchAsync(
            WorkflowTrigger.For(WorkflowTriggerKind.HumanAction, instanceId, NodeThreeWayMatchWorkflowSeed.MatchingState,
                "{\"decision\":\"reject\"}"), forAnotherRecord));
        Assert.Equal(NodeThreeWayMatchWorkflowSeed.MatchingState, (await workflows.LoadAsync(instanceId))!.CurrentStep);

        await dispatcher.DispatchAsync(
            WorkflowTrigger.For(WorkflowTriggerKind.Event, instanceId, NodeThreeWayMatchWorkflowSeed.MatchingState),
            Harborline.Api.LocalNodeHost.Tests.Authorization.TestAuthorization.AllowedDecision(LocalTenant, instanceId));
        var parked = await workflows.LoadAsync(instanceId);
        var wrongLedger = Harborline.Api.LocalNodeHost.Tests.Authorization.TestAuthorization.AllowedDecision(
            LocalTenant, "wrong-journal", "journal-entry", TeamRolePermissions.LedgerPost);
        await Assert.ThrowsAsync<ArgumentException>(() => dispatcher.DispatchAsync(
            WorkflowTrigger.For(WorkflowTriggerKind.HumanAction, instanceId, NodeThreeWayMatchWorkflowSeed.ReviewState,
                "{\"decision\":\"approve\"}"),
            new WorkflowDispatchAuthority(
                Harborline.Api.LocalNodeHost.Tests.Authorization.TestAuthorization.AllowedDecision(LocalTenant, instanceId),
                wrongLedger)));
        Assert.Equal(WorkflowStatus.Parked, (await workflows.LoadAsync(instanceId))!.Status);
        Assert.Empty(await JournalEntriesAsync(sp));

        var deniedLedger = await Harborline.Api.LocalNodeHost.Tests.Authorization.TestAuthorization.Gate(false).DecideAsync(
            Harborline.Api.LocalNodeHost.Tests.Authorization.TestAuthorization.Write(LocalTenant).Request(
                AuthorizationOperation.Parse(TeamRolePermissions.LedgerPost), "journal-entry",
                NodeLedgerPostingEffect.JournalEntryIdFor(parked!).Value));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => dispatcher.DispatchAsync(
            WorkflowTrigger.For(WorkflowTriggerKind.HumanAction, instanceId, NodeThreeWayMatchWorkflowSeed.ReviewState,
                "{\"decision\":\"approve\"}"),
            new WorkflowDispatchAuthority(
                Harborline.Api.LocalNodeHost.Tests.Authorization.TestAuthorization.AllowedDecision(LocalTenant, instanceId),
                deniedLedger)));
        Assert.Equal(WorkflowStatus.Parked, (await workflows.LoadAsync(instanceId))!.Status);
        Assert.Empty(await JournalEntriesAsync(sp));
    }

    // ── Harness ──

    private static async Task PublishDefinitionSeedAsync(IWorkflowDefinitionStore store)
    {
        var lifecycle = TestAuthorization.WorkflowLifecycle(
            store, Harborline.Api.LocalNodeHost.Tests.Authorization.TestAuthorization.AllowGate(),
            Harborline.Api.LocalNodeHost.Tests.Authorization.TestAuthorization.RoleGate());
        var authority = new Harborline.Api.Foundation.Authorization.AuthorizationWriteContext(
            new ActorId("test-workflow-seed"), LocalTenant,
            new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero));
        var decision = await lifecycle.DecideAsync(NodeThreeWayMatchWorkflowSeed.DefinitionKey, authority);
        await NodeThreeWayMatchWorkflowSeed.EnsurePublishedAsync(
            lifecycle, LocalTenant.Value, decision);
    }

    private ServiceProvider NewProvider()
    {
        var services = new ServiceCollection();
        services.AddTestKernelClock();
        services.AddSingleton<IHarborlineEntityModule, FinancialLedgerEntityModule>();
        services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialPeriods.Data.FinancialPeriodsEntityModule>();
        services.AddSingleton<IHarborlineEntityModule, Harborline.Api.LocalNodeHost.Data.Audit.AuditEventEntityModule>();
        services.AddSingleton<IHarborlineEntityModule, WorkflowEntityModule>();
        services.AddDbContextFactory<LocalNodeDbContext>(o => o.UseSqlite($"Data Source={_dbPath};Pooling=False"));
        services.AddSingleton<IJournalStore>(sp => new NodeEfJournalStore(
            sp.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>(), NodeJournalWriteAdapters.Create()));
        services.AddNodeFinancialPosting();
        services.AddSingleton<IAccountResolver, AlwaysPostableAccountResolver>();
        services.AddSingleton<IPeriodResolver>(new InMemoryPeriodResolver());

        services.AddSingleton<IWorkflowStore, NodeEfWorkflowStore>();
        services.AddTestAuthorizationGate();
        services.AddDurableWorkflowEngine();

        var entityStore = new InMemoryEntityStore(new InMemoryAssetStorage(), TimeProvider.System);
        services.AddSingleton(entityStore);
        services.AddSingleton<IEntityStore>(new InMemoryEntityStoreReader(entityStore));
        services.AddEntityStoreWorkflowDefinitionStore(_ => entityStore);

        services.AddWorkflowEffectFactory(
            NodeLedgerPostingEffect.CapabilityRef, WorkflowEffectReach.Internal,
            (isp, req) => NodeLedgerPostingEffect.Build(req, isp.GetRequiredService<IJournalPostingService>()));
        // A fixed human confirmer, distinct from the (non-human) engine proposer, so SoD passes.
        services.AddSingleton<IWorkflowConfirmationContext>(
            new FixedConfirmation(new WorkflowConfirmerIdentity(HumanConfirmer, IsHuman: true)));
        services.AddDeclarativeWorkflowInterpreter();

        return services.BuildServiceProvider();
    }

    private static async Task<List<JournalEntry>> JournalEntriesAsync(IServiceProvider sp)
    {
        var factory = sp.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
        await using var ctx = await factory.CreateDbContextAsync();
        return await ctx.Set<JournalEntry>().Where(j => j.TenantId == LocalTenant).ToListAsync();
    }

    private static async Task<int> CountJournalEntriesAsync(IServiceProvider sp)
        => (await JournalEntriesAsync(sp)).Count;

    private sealed class FixedConfirmation(WorkflowConfirmerIdentity confirmer) : IWorkflowConfirmationContext
    {
        public WorkflowProposerIdentity EngineProposer { get; } =
            new(NodeWorkflowConfirmationContext.EngineServicePartyId, IsHuman: false);

        public ValueTask<WorkflowConfirmerIdentity> ResolveConfirmerAsync(CancellationToken ct = default)
            => ValueTask.FromResult(confirmer);
    }
}
