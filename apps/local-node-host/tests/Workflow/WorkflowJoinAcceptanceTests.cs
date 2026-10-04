using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Blocks.FinancialLedger.Data;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialLedger.Services;
using Harborline.Api.Blocks.FinancialPeriods.Data;
using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Audit;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Search.Generation;
using Harborline.Api.LocalNodeHost.Data.Workflow;
using Harborline.Api.LocalNodeHost.Tests.Authorization;
using Harborline.Kernel.Core;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Workflow;

/// <summary>
/// Acceptance coverage for DES-0029's one-boundary workflow rule: a joined journal command, its workflow
/// advance, and their audit rows have one commit point and preserve carried actor authority.
/// </summary>
public sealed class WorkflowJoinAcceptanceTests : IAsyncLifetime
{
    private static readonly TenantId Tenant = new("workflow-join-acceptance");
    private static readonly DateTimeOffset At = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    private string _directory = null!;
    private ServiceProvider _services = null!;
    private IDbContextFactory<LocalNodeDbContext> _factory = null!;
    private NodeEfWorkflowStore _store = null!;
    private readonly WorkflowAuditFailureInterceptor _auditFailure = new();

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _directory = Path.Combine(Path.GetTempPath(), "workflow-join-acceptance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        var services = new ServiceCollection();
        services.AddSingleton<IHarborlineEntityModule, WorkflowEntityModule>();
        services.AddSingleton<IHarborlineEntityModule, AuditEventEntityModule>();
        services.AddSingleton<IHarborlineEntityModule, FinancialLedgerEntityModule>();
        services.AddSingleton<IHarborlineEntityModule, FinancialPeriodsEntityModule>();
        services.AddDbContextFactory<LocalNodeDbContext>(options =>
            options.UseSqlite($"Data Source={Path.Combine(_directory, "workflow.db")};Pooling=False")
                .AddInterceptors(_auditFailure));
        _services = services.BuildServiceProvider();
        _factory = _services.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
        _store = new NodeEfWorkflowStore(_factory);
        await using var context = await _factory.CreateDbContextAsync();
        await context.Database.EnsureCreatedAsync();
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
        catch { /* Best effort for SQLite handles released asynchronously by the provider. */ }
    }

    [Fact(DisplayName = "T-1002: a failed JoinAsync dooms the outer workflow execution and persists neither advance nor journal")]
    public async Task JoinFailure_DoomsOuterExecution_AndCommitsNothing()
    {
        await SeedInstanceAsync("join-failure");
        var key = new WorkflowStepKey("join-failure", 0, "approve");
        var effect = new WorkflowEffect(async (_, ct) =>
        {
            try
            {
                await KernelTransactionBoundary.JoinAsync(
                    Command("failed-join", "human-join"), new FailingParticipant(), ct);
            }
            catch (InjectedJoinFailure)
            {
                // A participant failure is intentionally caught here: the boundary must still remember that
                // it was doomed and refuse the outer commit.
            }
        });

        var failure = await Assert.ThrowsAsync<KernelTransactionStateException>(() => AdvanceAsync(key, effect));

        Assert.Equal(KernelTransactionErrors.EnclosingExecutionDoomed, failure.Code);
        await AssertUnadvancedAndJournalFreeAsync("join-failure");
    }

    [Fact(DisplayName = "T-1002: a fault after a joined journal row is staged rolls back that row and the workflow advance")]
    public async Task FaultAfterJoinedRowsAreStaged_RollsBackEverything()
    {
        await SeedInstanceAsync("staged-fault");
        var key = new WorkflowStepKey("staged-fault", 0, "approve");
        var effect = new WorkflowEffect(async (uow, ct) =>
        {
            await StageJoinedDraftAsync((LocalNodeDbContext)uow, "JE-staged-fault", "human-fault", ct);
            throw new InjectedJoinFailure();
        });

        await Assert.ThrowsAsync<InjectedJoinFailure>(() => AdvanceAsync(key, effect));

        await AssertUnadvancedAndJournalFreeAsync("staged-fault");
    }

    [Fact]
    public async Task WorkflowAuditFailure_AfterJoinedRowsAreFlushed_RollsBackEverything()
    {
        await SeedInstanceAsync("flushed-fault");
        var key = new WorkflowStepKey("flushed-fault", 0, "approve");
        var effect = new WorkflowEffect((uow, ct) =>
            StageJoinedDraftAsync((LocalNodeDbContext)uow, "JE-flushed-fault", "human-fault", ct));
        _auditFailure.Enabled = true;

        await Assert.ThrowsAsync<InjectedJoinFailure>(() => AdvanceAsync(key, effect));

        Assert.True(_auditFailure.SawFlushedJournalAudit);
        await AssertUnadvancedAndJournalFreeAsync("flushed-fault");
        await using var context = await _factory.CreateDbContextAsync();
        Assert.Empty(await context.Set<WorkflowEventRecord>().ToListAsync());
        Assert.Empty(await context.Set<WorkflowStepIdempotencyRecord>().ToListAsync());
    }

    [Fact(DisplayName = "T-1002: nested workflow execution is refused and cannot commit either workflow advance")]
    public async Task NestedExecution_IsRefused_AndCommitsNothing()
    {
        await SeedInstanceAsync("outer");
        await SeedInstanceAsync("inner");
        var outer = new WorkflowStepKey("outer", 0, "approve");
        var inner = new WorkflowStepKey("inner", 0, "approve");
        var effect = new WorkflowEffect((_, _) => AdvanceAsync(inner, effect: null));

        var failure = await Assert.ThrowsAsync<KernelTransactionStateException>(() => AdvanceAsync(outer, effect));

        Assert.Equal(KernelTransactionErrors.NestedExecution, failure.Code);
        await AssertUnadvancedAndJournalFreeAsync("outer");
        await AssertUnadvancedAndJournalFreeAsync("inner");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndependentEffect_CompletesBeforeWorkflowTransaction(bool withAuthority)
    {
        await SeedInstanceAsync("independent-effect");
        var calls = 0;
        var effect = new WorkflowEffect(async (uow, _) =>
        {
            Assert.Null(uow);
            calls++;
            // This separate SQLite writer must complete before either advance path holds a writer.
            await SeedInstanceAsync("independent-write");
        }, commitsIndependently: true);

        await _store.AdvanceAsync(
            new WorkflowStepKey("independent-effect", 0, "approve"), effect,
            "{}", "Advanced", "{}", "done", WorkflowStatus.Completed, At,
            withAuthority ? AuthorityFor("independent-effect") : null);

        Assert.Equal(1, calls);
        Assert.NotNull(await _store.LoadAsync("independent-write"));
        Assert.Equal(WorkflowStatus.Completed, (await _store.LoadAsync("independent-effect"))!.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndependentEffect_FailureLeavesWorkflowUnadvanced(bool withAuthority)
    {
        await SeedInstanceAsync("independent-failure");
        var effect = new WorkflowEffect((_, _) => Task.FromException(new InjectedJoinFailure()), commitsIndependently: true);

        await Assert.ThrowsAsync<InjectedJoinFailure>(() => _store.AdvanceAsync(
            new WorkflowStepKey("independent-failure", 0, "approve"), effect,
            "{}", "Advanced", "{}", "done", WorkflowStatus.Completed, At,
            withAuthority ? AuthorityFor("independent-failure") : null));

        await AssertUnadvancedAndJournalFreeAsync("independent-failure");
    }

    [Theory(DisplayName = "T-1002: KG approval co-commits Workflow.Advanced and Financial.JournalDrafted under the approving human")]
    [InlineData(0)]
    [InlineData(5)] // A non-UTC authority time must still hash in the stored UTC form, or the chain fails to verify.
    public async Task KgApproval_CoCommitsDraftAndAdvance_WithApprovingHumanActor(int offsetHours)
    {
        var cutover = CreateKgCutover();
        var authority = new AuthorizationWriteContext(new ActorId("kg-approving-human"), Tenant, AdmittedInstant.FromRecordedAct(At.ToOffset(TimeSpan.FromHours(offsetHours))));
        var parked = await cutover.ParkForApprovalAsync(
            Tenant,
            "kg-approval",
            new KgGenerationProposal(
                "Draft the approved journal.", "test-model", "1", ["record-1"],
                KgProposalTaint.UntrustedDerived,
                new KgProposedAction(KgProposedAction.DraftJournalEntry, "Draft", "{}")),
            new KgActionExecutionInput("1100", "4000", 12m, "approved draft", false));

        var result = await cutover.ResumeAsync(parked.InstanceId, "approve", note: null, authority: authority);

        Assert.Equal(WorkflowDispatchResult.Advanced, result);
        await using var context = await _factory.CreateDbContextAsync();
        Assert.Single(await context.Set<JournalEntry>().Where(entry => entry.SourceReference == "kg-action:" + parked.InstanceId).ToListAsync());
        var auditRows = await context.Set<NodeAuditEventRow>().Where(row => row.TenantId == Tenant.Value).ToListAsync();
        Assert.Equal("kg-approving-human", Assert.Single(auditRows, row => row.EventType == "Workflow.Advanced").Actor);
        Assert.Equal("kg-approving-human", Assert.Single(auditRows, row => row.EventType == NodeAuditWriteEnlister.JournalDraftedEventType).Actor);
        var ordered = await context.Set<NodeAuditEventRow>().FromSql($"""
            SELECT * FROM node_audit_events WHERE "TenantId" = {Tenant.Value} ORDER BY rowid
            """).ToListAsync();
        Assert.Equal("Financial.JournalDrafted", ordered[0].EventType);
        Assert.Equal("Workflow.Advanced", ordered[1].EventType);
        Assert.Equal(ordered[0].Hash, ordered[1].PrevHash);
        Assert.True(NodeAuditHashChain.Verify(ordered));
    }

    [Fact(DisplayName = "T-1002: KG approval without request authority is refused before it stages a journal or advance")]
    public async Task KgApproval_WithoutAuthority_IsRefused_AndCommitsNothing()
    {
        var cutover = CreateKgCutover();
        var parked = await cutover.ParkForApprovalAsync(
            Tenant,
            "kg-no-authority",
            new KgGenerationProposal(
                "Draft only with a human.", "test-model", "1", ["record-2"],
                KgProposalTaint.UntrustedDerived,
                new KgProposedAction(KgProposedAction.DraftJournalEntry, "Draft", "{}")),
            new KgActionExecutionInput("1100", "4000", 13m, "not approved", false));

        await Assert.ThrowsAsync<InvalidOperationException>(() => cutover.ResumeAsync(parked.InstanceId, "approve", note: null));

        await AssertUnadvancedAndJournalFreeAsync(parked.InstanceId, expectedStep: GraphRagProposalSteps.Approve, expectedStatus: WorkflowStatus.Parked);
    }

    [Fact(DisplayName = "T-1002: recurring generation records sys.scheduler as both advance and journal actor")]
    public async Task RecurringGeneration_RecordsSchedulerActor()
    {
        const string instanceId = "recurring-scheduler";
        var occurrence = new DateOnly(2026, 9, 30);
        await SeedInstanceAsync(instanceId, "generate@2026-09-30", "{" + "\"scheduleId\":\"schedule-a\",\"amount\":20,\"debitAccount\":\"1100\",\"creditAccount\":\"4000\"}");
        await WorkflowJoinTestComposition.SeedFinancialPrerequisitesAsync(_factory);
        var instance = (await _store.LoadAsync(instanceId))!;
        var journalId = NodeRecurringGenerationContext.JournalEntryIdFor(instance, occurrence).Value;
        var schedulerDecision = TestAuthorization.AllowedDecision(
            Tenant, journalId, "journal-entry", TeamRolePermissions.LedgerPost, WorkflowScheduleDaemon.SchedulerPrincipal, At);
        var workflowDecision = TestAuthorization.AllowedDecision(
            Tenant, instanceId, "record", TeamRolePermissions.RecordsWrite, WorkflowScheduleDaemon.SchedulerPrincipal, At);
        var posting = new JournalPostingService(
            new NodeEfAccountResolver(_factory),
            new NodeEfPeriodResolver(_factory),
            new NodeEfJournalStore(_factory, NodeJournalWriteAdapters.Create()),
            TestAuthorization.AllowGate());
        var effect = new NodeRecurringGenerationContext(new NodeAuditWriteEnlister(), posting)
            .BuildGenerationEffect(instance, occurrence, new WorkflowStepKey(instanceId, 0, "generate@2026-09-30"), At, schedulerDecision);

        await AdvanceAsync(new WorkflowStepKey(instanceId, 0, "generate@2026-09-30"), effect,
            new WorkflowDispatchAuthority(workflowDecision, schedulerDecision));

        await using var context = await _factory.CreateDbContextAsync();
        var auditRows = await context.Set<NodeAuditEventRow>().Where(row => row.TenantId == Tenant.Value).ToListAsync();
        Assert.Equal(WorkflowScheduleDaemon.SchedulerPrincipal,
            Assert.Single(auditRows, row => row.EventType == "Workflow.Advanced").Actor);
        Assert.Equal(WorkflowScheduleDaemon.SchedulerPrincipal,
            Assert.Single(auditRows, row => row.EventType == NodeAuditWriteEnlister.JournalPostedEventType).Actor);
    }

    private NodeKgActionApprovalCutover CreateKgCutover()
    {
        var handler = new GraphRagProposalHandler(new NodeKgActionApprovalContext(new NodeAuditWriteEnlister()));
        var dispatcher = new WorkflowTriggerDispatcher(_store, [handler]);
        return new NodeKgActionApprovalCutover(
            new NodeWorkflowInstantiationService(_store, _factory),
            dispatcher,
            TimeProvider.System,
            _store,
            TestAuthorization.AllowGate());
    }

    private async Task AdvanceAsync(
        WorkflowStepKey key,
        WorkflowEffect? effect,
        WorkflowDispatchAuthority? authority = null)
    {
        authority ??= AuthorityFor(key.InstanceId);
        await _store.AdvanceAsync(
            key,
            effect,
            "{}",
            "Advanced",
            "{}",
            "done",
            WorkflowStatus.Completed,
            At,
            authority);
    }

    private static WorkflowDispatchAuthority AuthorityFor(string instanceId) =>
        new(TestAuthorization.AllowedDecision(Tenant, instanceId));

    private async Task SeedInstanceAsync(string id, string step = "approve", string stateJson = "{}") =>
        await _store.CreateInstanceAsync(new WorkflowInstanceRecord
        {
            Id = id,
            TenantId = Tenant.Value,
            DefinitionKey = "workflow-join-acceptance",
            DefinitionVersion = "v1",
            CurrentStep = step,
            Status = WorkflowStatus.Running,
            StateJson = stateJson,
            CreatedAt = At,
            UpdatedAt = At,
        });

    private async Task AssertUnadvancedAndJournalFreeAsync(
        string instanceId,
        string expectedStep = "approve",
        WorkflowStatus expectedStatus = WorkflowStatus.Running)
    {
        var instance = await _store.LoadAsync(instanceId);
        Assert.NotNull(instance);
        Assert.Equal(expectedStep, instance.CurrentStep);
        Assert.Equal(expectedStatus, instance.Status);
        await using var context = await _factory.CreateDbContextAsync();
        Assert.Empty(await context.Set<JournalEntry>().Where(entry => entry.TenantId == Tenant).ToListAsync());
        Assert.Empty(await context.Set<NodeAuditEventRow>().Where(row => row.TenantId == Tenant.Value).ToListAsync());
    }

    private static async Task StageJoinedDraftAsync(
        LocalNodeDbContext context,
        string entryId,
        string actor,
        CancellationToken ct)
    {
        var entry = new JournalEntry(
            new JournalEntryId(entryId),
            Tenant,
            DateOnly.FromDateTime(At.UtcDateTime),
            "joined draft",
            [new(new GLAccountId("1100"), 1m, 0m), new(new GLAccountId("4000"), 0m, 1m)],
            new Instant(At),
            "workflow:" + entryId)
        {
            Status = JournalEntryStatus.Draft,
        };
        var auditId = Guid.NewGuid().ToString("D");
        context.Set<NodeAuditEventRow>().Add(new NodeAuditEventRow
        {
            AuditId = auditId,
            TenantId = Tenant.Value,
            EventType = NodeAuditWriteEnlister.JournalDraftedEventType,
            OccurredAt = At,
            Actor = actor,
            Payload = "{}",
            Hash = auditId,
        });
        await KernelTransactionBoundary.JoinAsync(
            NodeJournalKernelTransactionPort.Command(context, entry),
            NodeJournalKernelTransactionPort.CreateParticipant(context),
            ct);
    }

    private static KernelCommand<string> Command(string id, string actor) => new(
        new KernelOperationIdentity(id, id, id),
        id,
        new KernelAuditEvidence(id, actor, At, Array.Empty<byte>()));

    private sealed class InjectedJoinFailure : Exception
    {
    }

    private sealed class WorkflowAuditFailureInterceptor : SaveChangesInterceptor
    {
        internal bool Enabled { get; set; }
        internal bool SawFlushedJournalAudit { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var audits = eventData.Context!.ChangeTracker.Entries<NodeAuditEventRow>().ToList();
            if (Enabled && audits.Any(entry => entry.State == EntityState.Added && entry.Entity.EventType == "Workflow.Advanced"))
            {
                SawFlushedJournalAudit = audits.Any(entry => entry.State == EntityState.Unchanged
                    && entry.Entity.EventType == "Financial.JournalDrafted");
                throw new InjectedJoinFailure();
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FailingParticipant : IKernelTransactionParticipant<string>
    {
        public ValueTask StageOperationAsync(KernelOperationIdentity operation, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask StageRecordAsync(string record, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new InjectedJoinFailure());

        public ValueTask StageAuditAsync(KernelAuditEvidence audit, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
