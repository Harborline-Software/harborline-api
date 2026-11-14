using System.Text.Json;
using System.Text.Json.Nodes;
using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Scheduling;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Workflow;
using Harborline.Api.LocalNodeHost.Tests.Authorization;
using Harborline.Api.LocalNodeHost.Tests.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Workflow;

public sealed partial class WorkflowScheduledDefinitionTests
{
    [Fact]
    public async Task Changed_definition_is_refused_after_source_read_and_other_instances_continue()
    {
        var directory = Path.Combine(Path.GetTempPath(), "workflow-admission-" + Guid.NewGuid().ToString("N"));
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
                {"initialState":"waiting","states":[{"id":"waiting","kind":"Normal"},{"id":"done","kind":"Terminal"}],
                 "triggers":[{"id":"daily","kind":"Schedule","rrule":"FREQ=DAILY"}],
                 "transitions":[{"id":"finish","from":"waiting","on":"daily","to":"done"}],"actions":[],"guards":[]}
                """);
            var definitions = provider.GetRequiredService<IWorkflowDefinitionStore>();
            foreach (var key in new[] { "healthy", "refused" })
            {
                await definitions.RegisterAsync(ScheduledDefinition("FREQ=DAILY", key), authored.RootElement);
                await definitions.PublishAsync(new DefinitionCoordinates(new TenantId("local"), key, "1.0.0"));
            }
            var store = provider.GetRequiredService<IWorkflowStore>();
            foreach (var (id, key) in new[] { ("first", "healthy"), ("middle", "refused"), ("third", "healthy") })
                await store.CreateInstanceAsync(new WorkflowInstanceRecord
                {
                    Id = id, TenantId = "local", DefinitionKey = key, DefinitionVersion = "1.0.0",
                    CurrentStep = "waiting", Status = WorkflowStatus.Running, StateJson = "{}",
                }, clock.UtcNow);

            var entities = provider.GetRequiredService<InMemoryEntityStore>();
            var rows = new List<Entity>();
            await foreach (var row in entities.QueryAsync(new EntityQuery(
                Tenant: global::Harborline.Api.Foundation.MultiTenancy.TenantSelection.Of(new TenantId("local")),
                BodyContains: """{"kind":"workflow-definition","key":"refused","status":"Published"}""")))
                rows.Add(row);
            var definition = Assert.Single(rows);
            var validBody = definition.Body.RootElement.GetRawText();
            var historical = JsonNode.Parse(validBody)!;
            historical["authored"]!["triggers"]![0]!["rrule"] = "FREQ=DAILY;EXDATE=20260106";
            clock.UtcNow = clock.UtcNow.AddDays(1);
            var source = new ChangingDefinitionSource(new NodeRecurringScheduleSource(factory,
                provider.GetRequiredService<IRruleExpansionService>(), provider.GetRequiredService<IWorkflowDefinitionExecutionStore>()),
                entities, definition.Id, validBody, historical.ToJsonString(), clock);
            var dispatcher = new RefusingScheduleDispatcher("none");
            var log = new AdmissionLog();
            var daemon = new WorkflowScheduleDaemon(source, dispatcher, clock, log,
                authorizationGate: TestAuthorization.AllowGate(), store: store,
                definitions: provider.GetRequiredService<IWorkflowDefinitionExecutionStore>());

            await daemon.TickAsync();
            await daemon.TickAsync();

            Assert.Equal(new[] { "first", "middle", "third" }, source.Reads[0]);
            Assert.Equal(new[] { "first", "middle", "third" }, source.Reads[1]);
            Assert.Equal(new[] { "first", "third", "first", "third" }, dispatcher.Advanced);
            var refused = (await store.LoadAsync("middle"))!;
            Assert.Equal("waiting", refused.CurrentStep);
            Assert.Equal(0, refused.Iteration);
            Assert.Equal("{}", refused.StateJson);
            Assert.Equal(2, log.Errors.Count);
            Assert.All(log.Errors, error => Assert.Equal("workflow.admission.schedule_rrule_invalid",
                Assert.Single(Assert.IsType<WorkflowAdmissionException>(error).Result.Violations).Code));
            Assert.All(log.Messages, message =>
            {
                Assert.Contains("middle", message);
                Assert.Contains("waiting", message);
                Assert.Contains("WorkflowAdmissionException", message);
            });
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { /* best effort */ }
        }
    }

    private sealed class ChangingDefinitionSource(IWorkflowScheduleSource inner, InMemoryEntityStore entities,
        EntityId id, string validBody, string invalidBody, MutableClock clock) : IWorkflowScheduleSource
    {
        public List<string[]> Reads { get; } = [];
        public async Task<IReadOnlyList<WorkflowTrigger>> GetDueTriggersAsync(DateTimeOffset at, CancellationToken ct = default)
        {
            await ChangeAsync(validBody, ct);
            var due = (await inner.GetDueTriggersAsync(at, ct)).OrderBy(t => t.InstanceId, StringComparer.Ordinal).ToArray();
            Reads.Add(due.Select(t => t.InstanceId).ToArray());
            await ChangeAsync(invalidBody, ct);
            return due;
        }
        private async Task ChangeAsync(string body, CancellationToken ct)
        {
            var current = (await entities.GetAsync(id, ct: ct))!;
            using var document = JsonDocument.Parse(body);
            await ((IEntityMutationStore)entities).UpdateAsync(id, document,
                new UpdateOptions(new ActorId("node:historical-workflow-fixture"), current.CurrentVersion, clock.UtcNow), ct);
        }
    }

    private sealed class AdmissionLog : ILogger<WorkflowScheduleDaemon>
    {
        public List<Exception?> Errors { get; } = [];
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Errors.Add(exception);
            Messages.Add(formatter(state, exception));
        }
    }
}
