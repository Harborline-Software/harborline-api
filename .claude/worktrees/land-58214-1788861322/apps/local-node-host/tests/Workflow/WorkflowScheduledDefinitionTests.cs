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
using Harborline.Api.LocalNodeHost.Data.Search;
using Harborline.Api.LocalNodeHost.Data.Workflow;
using Harborline.Api.LocalNodeHost.Tests.Search;

using Xunit;

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
    public async Task Authored_schedule_advances_instance_when_time_becomes_due()
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
                .InstallAsync(tenant, clock.UtcNow, AuthorizationSeedProfile.Production);

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
            });

            clock.UtcNow = clock.UtcNow.AddDays(1);
            var source = new NodeRecurringScheduleSource(
                factory,
                provider.GetRequiredService<IRruleExpansionService>(),
                provider.GetRequiredService<IWorkflowDefinitionExecutionStore>());
            var dispatcher = provider.GetRequiredService<IWorkflowTriggerDispatcher>();
            var daemon = new WorkflowScheduleDaemon(
                source,
                dispatcher,
                clock,
                NullLogger<WorkflowScheduleDaemon>.Instance,
                authorizationGate: provider.GetRequiredService<AuthorizationGate>(),
                store: instances,
                definitions: provider.GetRequiredService<IWorkflowDefinitionExecutionStore>());

            await daemon.TickAsync();

            var advanced = await instances.LoadAsync("scheduled-instance");
            Assert.Equal(WorkflowStatus.Completed, advanced!.Status);
            Assert.Equal("done", advanced.CurrentStep);

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
            });
            clock.UtcNow = clock.UtcNow.AddDays(1);

            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => daemon.TickAsync());

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

    private static ServiceProvider NewProvider(
        string databasePath,
        TimeProvider clock,
        IDbContextFactory<NodeLocalSearchDbContext> searchFactory)
    {
        var services = new ServiceCollection();
        services.AddSingleton(clock);
        services.AddSingleton<IHarborlineEntityModule, WorkflowEntityModule>();
        services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialLedger.Data.FinancialLedgerEntityModule>();
        services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialPeriods.Data.FinancialPeriodsEntityModule>(); // the ledger module is never registered without periods
        services.AddDbContextFactory<LocalNodeDbContext>(options =>
            options.UseSqlite($"Data Source={databasePath};Pooling=False"));
        services.AddSingleton<IWorkflowStore, NodeEfWorkflowStore>();
        services.AddSingleton(searchFactory);
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
