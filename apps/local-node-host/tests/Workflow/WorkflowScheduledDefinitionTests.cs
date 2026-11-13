using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Blocks.Workflow.Interpreter;
using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.Foundation.Scheduling;
using Harborline.Api.Foundation.Scheduling.DependencyInjection;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Authorization;
using Harborline.Api.LocalNodeHost.Data.Audit;
using Harborline.Api.LocalNodeHost.Data.Search;
using Harborline.Api.LocalNodeHost.Data.Workflow;
using Harborline.Api.LocalNodeHost.Tests.Search;

using Xunit;

using Harborline.Api.LocalNodeHost.Tests.Authorization;

namespace Harborline.Api.LocalNodeHost.Tests.Workflow;

public sealed class WorkflowScheduledDefinitionTests
{
    [Fact]
    public void Admission_refuses_schedule_trigger_without_rrule()
    {
        IWorkflowAdmissionValidator admission = new WorkflowAdmissionValidator();

        var result = admission.Validate(ScheduledDefinition(rrule: null));

        var violation = Assert.Single(result.Violations);
        Assert.Equal(WorkflowAdmissionCodes.ScheduleRruleInvalid, violation.Code);
        Assert.Equal("daily", violation.Locator);
    }

    [Fact]
    public void Admission_refuses_unparseable_schedule_rrule()
    {
        IWorkflowAdmissionValidator admission = new WorkflowAdmissionValidator();

        var result = admission.Validate(ScheduledDefinition("FREQ=NEVER"));

        var violation = Assert.Single(result.Violations);
        Assert.Equal(WorkflowAdmissionCodes.ScheduleRruleInvalid, violation.Code);
        Assert.Equal("daily", violation.Locator);
    }

    [Fact]
    public Task Authored_schedule_advances_instance_when_time_becomes_due()
        => AssertAuthoredScheduleAdvancesAsync(recordsOnly: false);

    [Fact]
    public Task State_only_schedule_requires_only_records_write()
        => AssertAuthoredScheduleAdvancesAsync(recordsOnly: true);

    private static async Task AssertAuthoredScheduleAdvancesAsync(bool recordsOnly)
    {
        var directory = Path.Combine(Path.GetTempPath(), "workflow-schedule-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var clock = new MutableClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
            await using var search = await SearchTestStore.CreateAsync();
            await using (var identity = search.CreateInstallationIdentityContext())
                await identity.Database.MigrateAsync();
            await using var provider = NewProvider(Path.Combine(directory, "local-node.db"), clock, search.Factory);
            var factory = provider.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
            await using (var context = await factory.CreateDbContextAsync())
            {
                await context.Database.EnsureCreatedAsync();
            }
            var tenant = new TenantId("local");
            await provider.GetRequiredService<AccessGrantAuthorizationSeed>()
                .InstallAsync(tenant, AdmittedInstant.Read(new MutableClock(clock.UtcNow)), AuthorizationSeedProfile.Production, TestDesktopOperator.Actor);

            const string rrule = "FREQ=DAILY";
            using var authored = JsonDocument.Parse(
                """
                {
                  "initialState": "waiting",
                  "states": [
                    { "id": "waiting", "kind": "Normal" },
                    { "id": "done", "kind": "Terminal" }
                  ],
                  "triggers": [
                    { "id": "daily", "kind": "Schedule", "rrule": "FREQ=DAILY" }
                  ],
                  "transitions": [
                    { "id": "finish", "from": "waiting", "on": "daily", "to": "done" }
                  ],
                  "actions": [],
                  "guards": []
                }
                """);

            var definitions = provider.GetRequiredService<IWorkflowDefinitionStore>();
            await definitions.RegisterAsync(ScheduledDefinition(rrule), authored.RootElement);
            await definitions.PublishAsync(new DefinitionCoordinates(
                new TenantId("local"), "scheduled-definition", "1.0.0"));

            var instances = provider.GetRequiredService<IWorkflowStore>();
            await instances.CreateInstanceAsync(new WorkflowInstanceRecord
            {
                Id = "scheduled-instance",
                TenantId = "local",
                DefinitionKey = "scheduled-definition",
                DefinitionVersion = "1.0.0",
                CurrentStep = "waiting",
                Status = WorkflowStatus.Running,
                StateJson = "{}",
            }, clock.UtcNow);

            clock.UtcNow = clock.UtcNow.AddDays(1);
            var source = new NodeRecurringScheduleSource(
                factory,
                provider.GetRequiredService<IRruleExpansionService>(),
                provider.GetRequiredService<IWorkflowDefinitionExecutionStore>());
            var dispatcher = provider.GetRequiredService<IWorkflowTriggerDispatcher>();
            var requests = new List<AuthorizationGateRequest>();
            var daemon = new WorkflowScheduleDaemon(
                source,
                dispatcher,
                clock,
                NullLogger<WorkflowScheduleDaemon>.Instance,
                authorizationGate: recordsOnly
                    ? TestAuthorization.Gate(request => request.Act.Operation.Value == "records:write", requests.Add)
                    : provider.GetRequiredService<AuthorizationGate>(),
                store: instances,
                definitions: provider.GetRequiredService<IWorkflowDefinitionExecutionStore>());

            await daemon.TickAsync();

            var advanced = await instances.LoadAsync("scheduled-instance");
            Assert.Equal(WorkflowStatus.Completed, advanced!.Status);
            Assert.Equal("done", advanced.CurrentStep);
            if (recordsOnly)
            {
                Assert.Equal("records:write", Assert.Single(requests).Act.Operation.Value);
                daemon = new WorkflowScheduleDaemon(
                    source, dispatcher, clock, NullLogger<WorkflowScheduleDaemon>.Instance,
                    authorizationGate: provider.GetRequiredService<AuthorizationGate>(),
                    store: instances,
                    definitions: provider.GetRequiredService<IWorkflowDefinitionExecutionStore>());
            }

            var schedulerGrant = await provider.GetRequiredService<IGrantStore>()
                .FindBySourceReferenceAsync(tenant, AccessGrantAuthorizationSeed.SchedulerGrantSource);
            Assert.NotNull(schedulerGrant);
            await provider.GetRequiredService<IGrantStore>().RevokeAsync(
                tenant,
                schedulerGrant!.GrantId,
                new GrantRevocation(
                    new ActorId("tenant-administrator"), clock.UtcNow.AddMinutes(1),
                    new GrantReason(GrantReasonCodes.RevocationOffboarding)));
            await instances.CreateInstanceAsync(new WorkflowInstanceRecord
            {
                Id = "revoked-scheduler-instance",
                TenantId = tenant.Value,
                DefinitionKey = "scheduled-definition",
                DefinitionVersion = "1.0.0",
                CurrentStep = "waiting",
                Status = WorkflowStatus.Running,
                StateJson = "{}",
            }, clock.UtcNow);
            clock.UtcNow = clock.UtcNow.AddDays(1);

            await daemon.TickAsync();

            var denied = await instances.LoadAsync("revoked-scheduler-instance");
            Assert.Equal(WorkflowStatus.Running, denied!.Status);
            Assert.Equal("waiting", denied.CurrentStep);
            await using var verify = await factory.CreateDbContextAsync();
            Assert.Equal(0, await verify.Set<JournalEntry>().CountAsync());
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch { /* best effort */ }
        }
    }

    [Theory]
    [InlineData("authorization")]
    [InlineData("gate")]
    [InlineData("admission")]
    public async Task Refused_scheduled_instance_remains_due_on_the_next_tick(string kind)
    {
        var directory = Path.Combine(Path.GetTempPath(), "workflow-t1180-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var clock = new MutableClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
            await using var search = await SearchTestStore.CreateAsync();
            await using (var identity = search.CreateInstallationIdentityContext())
                await identity.Database.MigrateAsync();
            await using var provider = NewProvider(Path.Combine(directory, "node.db"), clock, search.Factory);
            var factory = provider.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
            await using (var context = await factory.CreateDbContextAsync())
                await context.Database.EnsureCreatedAsync();
            using var authored = JsonDocument.Parse("""
                {"initialState":"waiting", "states":[{"id":"waiting","kind":"Normal"},{"id":"done","kind":"Terminal"}],
                 "triggers":[{"id":"daily","kind":"Schedule","rrule":"FREQ=DAILY"}],
                 "transitions":[{"id":"finish","from":"waiting","on":"daily","to":"done"}], "actions":[], "guards":[]}
                """);
            var definitions = provider.GetRequiredService<IWorkflowDefinitionStore>();
            await definitions.RegisterAsync(ScheduledDefinition("FREQ=DAILY"), authored.RootElement);
            await definitions.PublishAsync(new DefinitionCoordinates(new TenantId("local"), "scheduled-definition", "1.0.0"));
            var store = provider.GetRequiredService<IWorkflowStore>();
            foreach (var id in new[] { "first", "middle", "third" })
                await store.CreateInstanceAsync(new WorkflowInstanceRecord
                {
                    Id = id, TenantId = "local", DefinitionKey = "scheduled-definition", DefinitionVersion = "1.0.0",
                    CurrentStep = "waiting", Status = WorkflowStatus.Running, StateJson = "{}",
                }, clock.UtcNow);
            clock.UtcNow = clock.UtcNow.AddDays(1);
            var source = new OrderedScheduleSource(new NodeRecurringScheduleSource(factory,
                provider.GetRequiredService<IRruleExpansionService>(), provider.GetRequiredService<IWorkflowDefinitionExecutionStore>()));
            var dispatcher = new RefusingScheduleDispatcher(kind);
            var daemon = new WorkflowScheduleDaemon(source, dispatcher, clock, NullLogger<WorkflowScheduleDaemon>.Instance,
                authorizationGate: TestAuthorization.Gate(request => kind != "authorization" || request.Target.RecordId != "middle"),
                store: store);
            var firstError = await Record.ExceptionAsync(() => daemon.TickAsync());
            var secondError = await Record.ExceptionAsync(() => daemon.TickAsync());
            Assert.True(firstError is null && secondError is null,
                $"Tick aborted: {firstError?.GetType().Name}; dispatched: {string.Join(",", dispatcher.Advanced)}; second: {secondError?.GetType().Name}");
            Assert.Equal(new[] { "first", "middle", "third" }, source.Reads[0]);
            Assert.Equal(new[] { "first", "middle", "third" }, source.Reads[1]);
            Assert.Equal(new[] { "first", "third", "first", "third" }, dispatcher.Advanced);
            var refused = await store.LoadAsync("middle");
            Assert.Equal(WorkflowStatus.Running, refused!.Status);
            Assert.Equal("waiting", refused.CurrentStep);
            Assert.Equal("{}", refused.StateJson);
            Assert.Equal(0, refused.Iteration);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Refused_occurrence_does_not_let_a_later_occurrence_advance_its_cursor()
    {
        var directory = Path.Combine(Path.GetTempPath(), "workflow-t1180-cursor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var clock = new MutableClock(new DateTimeOffset(2026, 1, 7, 0, 0, 0, TimeSpan.Zero));
            await using var search = await SearchTestStore.CreateAsync();
            await using (var identity = search.CreateInstallationIdentityContext())
                await identity.Database.MigrateAsync();
            await using var provider = NewProvider(Path.Combine(directory, "node.db"), clock, search.Factory);
            var factory = provider.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
            await using (var context = await factory.CreateDbContextAsync())
                await context.Database.EnsureCreatedAsync();
            var store = provider.GetRequiredService<IWorkflowStore>();
            foreach (var (id, start) in new[] { ("SCHED-A", "2026-01-06"), ("SCHED-B", "2026-01-07") })
                await store.CreateInstanceAsync(new WorkflowInstanceRecord
                {
                    Id = "recurring-generation:" + id, TenantId = "local", DefinitionKey = "recurring-generation",
                    DefinitionVersion = "1.0.0", CurrentStep = "waiting", Status = WorkflowStatus.Running,
                    StateJson = $$"""{"scheduleId":"{{id}}","amount":1,"rrule":"FREQ=DAILY","startsOn":"{{start}}","timezone":"UTC"}""",
                }, clock.UtcNow);
            var source = new NodeRecurringScheduleSource(factory, provider.GetRequiredService<IRruleExpansionService>());
            var dispatcher = new WorkflowTriggerDispatcher(store, [new CursorHandler()]);
            var deny = true;
            var daemon = new WorkflowScheduleDaemon(source, dispatcher, clock, NullLogger<WorkflowScheduleDaemon>.Instance,
                authorizationGate: TestAuthorization.Gate(request => !deny || request.Target.RecordId != "JE-76656cda64fd5cecab4e9e4ce71a4ca2"),
                store: store);

            await daemon.TickAsync();
            Assert.Equal("waiting", (await store.LoadAsync("recurring-generation:SCHED-A"))!.CurrentStep);
            Assert.Equal("generate@2026-01-07", (await store.LoadAsync("recurring-generation:SCHED-B"))!.CurrentStep);
            Assert.Contains((await source.GetDueTriggersAsync(clock.UtcNow)), t => t.InstanceId == "recurring-generation:SCHED-A" && t.Step == "generate@2026-01-06");
            deny = false;
            await daemon.TickAsync();
            Assert.Equal("generate@2026-01-07", (await store.LoadAsync("recurring-generation:SCHED-A"))!.CurrentStep);
            await using var verify = await factory.CreateDbContextAsync();
            Assert.Equal(new[] { "generate@2026-01-06", "generate@2026-01-07" },
                await verify.Set<WorkflowStepIdempotencyRecord>().Where(r => r.InstanceId == "recurring-generation:SCHED-A")
                    .OrderBy(r => r.Step).Select(r => r.Step).ToArrayAsync());
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { /* best effort */ }
        }
    }

    private sealed class CursorHandler : IWorkflowStepHandler
    {
        public string DefinitionKey => "recurring-generation";
        public ValueTask<WorkflowStepOutcome> DecideAsync(WorkflowInstanceRecord instance, WorkflowTrigger trigger, CancellationToken ct = default)
            => ValueTask.FromResult(WorkflowStepOutcome.Advance(trigger.Step));
    }

    private sealed class OrderedScheduleSource(IWorkflowScheduleSource inner) : IWorkflowScheduleSource
    {
        public List<string[]> Reads { get; } = [];
        public async Task<IReadOnlyList<WorkflowTrigger>> GetDueTriggersAsync(DateTimeOffset asOf, CancellationToken ct = default)
        {
            var due = (await inner.GetDueTriggersAsync(asOf, ct)).OrderBy(trigger => trigger.InstanceId, StringComparer.Ordinal).ToArray();
            Reads.Add(due.Select(trigger => trigger.InstanceId).ToArray());
            return due;
        }
    }

    private sealed class RefusingScheduleDispatcher(string kind) : IWorkflowTriggerDispatcher
    {
        public List<string> Advanced { get; } = [];
        public Task<WorkflowDispatchResult> DispatchAsync(WorkflowTrigger trigger, CancellationToken ct = default)
        {
            if (trigger.InstanceId == "middle" && kind == "gate") throw new WorkflowGateUnenforcedException("gate");
            if (trigger.InstanceId == "middle" && kind == "admission") throw new WorkflowAdmissionException(new WorkflowAdmissionResult
            {
                Violations = [new("workflow.admission.gate_on_autonomous_step", "refused", "daily")],
            });
            Advanced.Add(trigger.InstanceId);
            return Task.FromResult(WorkflowDispatchResult.Advanced);
        }
        public Task<WorkflowDispatchResult> DispatchAsync(WorkflowTrigger trigger, AuthorizationDecision decision, CancellationToken ct = default)
            => DispatchAsync(trigger, ct);
        public Task<WorkflowDispatchResult> DispatchAsync(WorkflowTrigger trigger, WorkflowDispatchAuthority authority, CancellationToken ct = default)
            => DispatchAsync(trigger, ct);
    }

    private static ServiceProvider NewProvider(
        string databasePath,
        TimeProvider clock,
        IDbContextFactory<NodeLocalSearchDbContext> searchFactory)
    {
        var services = new ServiceCollection();
        services.AddSingleton(clock);
        services.AddSingleton<IHarborlineEntityModule, WorkflowEntityModule>();
        services.AddSingleton<IHarborlineEntityModule, AuditEventEntityModule>();
        services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialLedger.Data.FinancialLedgerEntityModule>();
        services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialPeriods.Data.FinancialPeriodsEntityModule>(); // the ledger module is never registered without periods
        services.AddDbContextFactory<LocalNodeDbContext>(options =>
            options.UseSqlite($"Data Source={databasePath};Pooling=False"));
        services.AddSingleton<IWorkflowStore, NodeEfWorkflowStore>();
        services.AddSingleton<INodeAuditWriteEnlister, NodeAuditWriteEnlister>();
        services.AddSingleton(searchFactory);
        TestAuthorization.AddMemberRosterConstraints(services);
        services.AddNodeAuthorizationModel();
        services.AddDurableWorkflowEngine();
        var entityStore = new InMemoryEntityStore(new InMemoryAssetStorage(), clock);
        services.AddSingleton(entityStore);
        services.AddSingleton<IEntityStore>(new InMemoryEntityStoreReader(entityStore));
        services.AddEntityStoreWorkflowDefinitionStore(_ => entityStore);
        services.AddSingleton<IWorkflowConfirmationContext>(new UnusedConfirmationContext());
        services.AddDeclarativeWorkflowInterpreter();
        services.AddFoundationScheduling();
        return services.BuildServiceProvider();
    }

    private static WorkflowDefinition ScheduledDefinition(string? rrule)
        => new()
        {
            Tenant = "local",
            Key = "scheduled-definition",
            Version = "1.0.0",
            InitialState = "waiting",
            States =
            [
                new WorkflowStateDef { Id = "waiting" },
                new WorkflowStateDef { Id = "done", Kind = WorkflowStateKind.Terminal },
            ],
            Triggers =
            [
                new WorkflowTriggerBindingDef
                {
                    Id = "daily",
                    Kind = WorkflowTriggerKind.Schedule,
                    Rrule = rrule,
                },
            ],
            Transitions =
            [
                new WorkflowTransitionDef { Id = "finish", From = "waiting", On = "daily", To = "done" },
            ],
        };

    private sealed class MutableClock(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class UnusedConfirmationContext : IWorkflowConfirmationContext
    {
        public WorkflowProposerIdentity EngineProposer { get; } = new(Guid.Empty, IsHuman: false);

        public ValueTask<WorkflowConfirmerIdentity> ResolveConfirmerAsync(CancellationToken ct = default)
            => throw new InvalidOperationException("The scheduled definition has no CP action.");
    }
}
