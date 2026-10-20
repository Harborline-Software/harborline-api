using System.Globalization;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Blocks.FinancialAr.Models;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Blocks.FinancialAr.Data;
using Harborline.Api.Blocks.FinancialLedger.Data;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Definitions;
// PartyId lives in People.Foundation.Models (used by the seeded schedule).
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.Foundation.Scheduling;
using Harborline.Api.Foundation.Scheduling.DependencyInjection;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Audit;
using Harborline.Api.LocalNodeHost.Data.Workflow;
using Harborline.Api.LocalNodeHost.Tests.Authorization;

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
        await WorkflowJoinTestComposition.SeedFinancialPrerequisitesAsync(factory);
    }

    public Task DisposeAsync()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort */ }
        return Task.CompletedTask;
    }

    [Theory]
    [InlineData("unsupported")]
    [InlineData("malformed")]
    [InlineData("cap")]
    public async Task Strict_recurrence_refusal_skips_only_the_bad_instance_and_healthy_monthly_dispatch_survives_reopen(string failure)
    {
        var factory = NewFactory();
        var store = NewStore(factory);
        var instantiation = NewInstantiation(store, factory);
        var at = new DateTimeOffset(2026, 1, 7, 0, 0, 0, TimeSpan.Zero);
        var rule = failure switch
        {
            "unsupported" => "FREQ=DAILY;EXDATE=20260106",
            "malformed" => "FREQ=DAILY;INTERVAL=0",
            "cap" => "FREQ=DAILY",
            _ => throw new InvalidOperationException("Unknown literal recurrence fixture."),
        };
        // The cap fixture has a genuinely unprocessed >1000-date backlog; its cursor must not
        // be silently moved to today. The healthy schedule remains entirely within the open period.
        var startsOn = failure == "cap" ? new DateOnly(2023, 1, 1) : new DateOnly(2026, 1, 1);
        await SeedScheduleAsync(factory, "SCHED-STRICT", rule, startsOn, 250m);
        await SeedScheduleAsync(factory, "SCHED-HEALTHY", "FREQ=MONTHLY;BYMONTHDAY=1", new DateOnly(2026, 1, 1), 250m);
        Assert.Equal(2, await instantiation.SyncRecurringGenerationProcessesAsync(LocalTenantId, at));
        const string instanceId = "recurring-generation:SCHED-STRICT";
        var before = (await store.LoadAsync(instanceId))!;
        Assert.Equal(WorkflowStatus.Running, before.Status);
        long eventsBefore;
        await using (var context = await factory.CreateDbContextAsync())
        {
            Assert.Equal(0, await context.Set<WorkflowStepIdempotencyRecord>().CountAsync());
            eventsBefore = await context.Set<WorkflowEventRecord>().LongCountAsync(row => row.InstanceId == instanceId);
        }
        var log = new ScheduleLog();
        var source = new NodeRecurringScheduleSource(factory, new InMemoryRruleExpansionService(), log);
        var due = await source.GetDueTriggersAsync(at);
        var healthy = Assert.Single(due);
        Assert.Equal("recurring-generation:SCHED-HEALTHY", healthy.InstanceId);
        Assert.Equal("generate@2026-01-01", healthy.Step);
        var refusal = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, refusal.Level);
        Assert.Contains("recurring-generation:SCHED-STRICT", refusal.Message, StringComparison.Ordinal);
        if (failure == "cap")
        {
            var error = Assert.IsType<global::Harborline.Foundation.Scheduling.RruleExpansionCapExceededException>(refusal.Error);
            Assert.Equal(1000, error.CandidateLimit);
            Assert.Equal(1001, error.CandidatesExamined);
        }
        else if (failure == "malformed") Assert.IsType<FormatException>(refusal.Error);
        else Assert.IsType<NotSupportedException>(refusal.Error);
        await NewRecurringDaemon(factory, store, source, at).TickAsync();
        Assert.Equal(1, await CountJournalEntriesAsync(factory));

        var restartedFactory = NewFactory();
        var restartedStore = NewStore(restartedFactory);
        await NewRecurringDaemon(restartedFactory, restartedStore,
            new NodeRecurringScheduleSource(restartedFactory, new InMemoryRruleExpansionService(), log), at).TickAsync();
        Assert.Equal(1, await CountJournalEntriesAsync(restartedFactory));
        var after = (await restartedStore.LoadAsync(instanceId))!;
        Assert.Equal(WorkflowStatus.Running, after.Status);
        Assert.Equal(before.CurrentStep, after.CurrentStep);
        Assert.Equal(0, after.Iteration);
        Assert.Equal(before.StateJson, after.StateJson);
        Assert.Equal(before.DefinitionVersion, after.DefinitionVersion);
        Assert.Equal(before.CreatedAt, after.CreatedAt);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        await using var fresh = await restartedFactory.CreateDbContextAsync();
        Assert.Equal(0, await fresh.Set<WorkflowStepIdempotencyRecord>().CountAsync(row => row.InstanceId == instanceId));
        Assert.Equal(1, await fresh.Set<WorkflowStepIdempotencyRecord>()
            .CountAsync(row => row.InstanceId == "recurring-generation:SCHED-HEALTHY"));
        Assert.Equal(eventsBefore, await fresh.Set<WorkflowEventRecord>().LongCountAsync(row => row.InstanceId == instanceId));
    }

    [Fact]
    public async Task Long_lived_daily_cursor_reyields_the_prior_occurrence_but_posts_only_two_new_dates_after_reopen()
    {
        var factory = NewFactory();
        var store = NewStore(factory);
        await SeedScheduleAsync(factory, "SCHED-LONG", "FREQ=DAILY", new DateOnly(2023, 1, 1), 250m);
        await NewInstantiation(store, factory).SyncRecurringGenerationProcessesAsync(LocalTenantId,
            new DateTimeOffset(2023, 1, 1, 0, 0, 0, TimeSpan.Zero));
        const string id = "recurring-generation:SCHED-LONG";
        // Establish a recent durable position through the real store, then actually dispatch/post
        // Jan5. The test does not fabricate a prior Jan5 idempotency receipt or financial effect.
        await store.AdvanceAsync(new WorkflowStepKey(id, 0, "generate@2023-01-01"), null, "{}", "Advanced", "{}",
            "generate@2026-01-05", WorkflowStatus.Running, new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero));
        await NewRecurringDaemon(factory, store,
            new NodeRecurringScheduleSource(factory, new InMemoryRruleExpansionService()),
            new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero)).TickAsync();
        Assert.Equal(1, await CountJournalEntriesAsync(factory));
        var at = new DateTimeOffset(2026, 1, 7, 0, 0, 0, TimeSpan.Zero);
        var log = new ScheduleLog();
        var source = new NodeRecurringScheduleSource(factory, new InMemoryRruleExpansionService(), log);
        var due = await source.GetDueTriggersAsync(at);
        Assert.Equal(new[] { "generate@2026-01-05", "generate@2026-01-06", "generate@2026-01-07" }, due.Select(row => row.Step));
        Assert.All(due, row => Assert.Equal(id, row.InstanceId));
        await NewRecurringDaemon(factory, store, source, at).TickAsync();
        Assert.Equal(3, await CountJournalEntriesAsync(factory)); // Jan5 replay plus exactly Jan6/7 newly posted.
        Assert.Empty(log.Entries);
        var beforeReopen = (await store.LoadAsync(id))!;
        Assert.Equal("generate@2026-01-07", beforeReopen.CurrentStep);
        Assert.Contains("2023-01-01", beforeReopen.StateJson, StringComparison.Ordinal);
        var reopenedFactory = NewFactory();
        var reopenedStore = NewStore(reopenedFactory);
        await NewRecurringDaemon(reopenedFactory, reopenedStore,
            new NodeRecurringScheduleSource(reopenedFactory, new InMemoryRruleExpansionService(), log), at).TickAsync();
        Assert.Equal(3, await CountJournalEntriesAsync(reopenedFactory));
        var afterReopen = (await reopenedStore.LoadAsync(id))!;
        Assert.Equal(beforeReopen.StateJson, afterReopen.StateJson);
        Assert.Equal(beforeReopen.UpdatedAt, afterReopen.UpdatedAt);
        await using var context = await reopenedFactory.CreateDbContextAsync();
        Assert.Equal(new[] { "generate@2026-01-05", "generate@2026-01-06", "generate@2026-01-07" },
            await context.Set<WorkflowStepIdempotencyRecord>().Where(row => row.InstanceId == id && row.Step != "generate@2023-01-01")
                .OrderBy(row => row.Step).Select(row => row.Step).ToArrayAsync());
    }

    [Theory]
    [InlineData("ar-SA", "th-TH")]
    [InlineData("th-TH", "ar-SA")]
    public async Task Stored_ISO_dates_drive_real_recurring_effects_and_replay_identically_under_non_Gregorian_cultures(
        string cultureName, string replayCultureName)
    {
        var priorCulture = CultureInfo.CurrentCulture;
        try
        {
            var culture = new CultureInfo(cultureName, useUserOverride: false);
            culture.DateTimeFormat.Calendar = cultureName == "ar-SA" ? new UmAlQuraCalendar() : new ThaiBuddhistCalendar();
            Assert.False(culture.DateTimeFormat.Calendar is GregorianCalendar);
            Assert.NotEqual("2026-01-05", new DateOnly(2026, 1, 5).ToString("yyyy-MM-dd", culture));
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            var factory = NewFactory();
            var store = NewStore(factory);
            var createdAt = new DateTimeOffset(2023, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var firstAt = new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero);
            var laterAt = new DateTimeOffset(2026, 1, 7, 0, 0, 0, TimeSpan.Zero);
            const string id = "recurring-generation:SCHED-ISO-CULTURE";
            await SeedScheduleAsync(factory, "SCHED-ISO-CULTURE", "FREQ=DAILY", new DateOnly(2023, 1, 1), 250m);
            Assert.Equal(1, await NewInstantiation(store, factory).SyncRecurringGenerationProcessesAsync(LocalTenantId, createdAt));
            var seeded = (await store.LoadAsync(id))!;
            using (var state = JsonDocument.Parse(seeded.StateJson))
            {
                Assert.Equal("2023-01-01", state.RootElement.GetProperty("startsOn").GetString());
                Assert.Equal("FREQ=DAILY", state.RootElement.GetProperty("rrule").GetString());
                Assert.Equal("UTC", state.RootElement.GetProperty("timezone").GetString());
                Assert.Equal("SCHED-ISO-CULTURE", state.RootElement.GetProperty("scheduleId").GetString());
                Assert.Equal(250m, state.RootElement.GetProperty("amount").GetDecimal());
            }
            // A real durable position move, not a fabricated Jan5 effect or receipt. Its old
            // DTSTART exceeds the strict producer cap unless the ISO cursor is read correctly.
            await store.AdvanceAsync(new WorkflowStepKey(id, 0, "generate@2023-01-01"), null, "{}", "Advanced", "{}",
                "generate@2026-01-05", WorkflowStatus.Running, firstAt);
            Assert.Equal(0, await CountJournalEntriesAsync(factory));
            var log = new ScheduleLog();
            var ledgerTargets = new List<string>();
            WorkflowScheduleDaemon Daemon(IDbContextFactory<LocalNodeDbContext> currentFactory,
                IWorkflowStore currentStore, IWorkflowScheduleSource source, DateTimeOffset at)
                => new(source, new WorkflowTriggerDispatcher(currentStore, new[] { RecurringHandler(currentFactory) }),
                    new FixedClock(at), NullLogger<WorkflowScheduleDaemon>.Instance,
                    authorizationGate: TestAuthorization.Gate(true, request =>
                    {
                        if (request.Target.RecordKind == "journal-entry") ledgerTargets.Add(request.Target.RecordId);
                    }), store: currentStore);

            CultureInfo.CurrentCulture = culture;
            var source = new NodeRecurringScheduleSource(factory, new InMemoryRruleExpansionService(), log);
            var first = Assert.Single(await source.GetDueTriggersAsync(firstAt));
            Assert.Equal(id, first.InstanceId);
            Assert.Equal("generate@2026-01-05", first.Step);
            await Daemon(factory, store, source, firstAt).TickAsync();
            Assert.Equal(1, await CountJournalEntriesAsync(factory));
            // Literal golden identity from the independently evaluated SHA-256/RFC-bit occurrence
            // contract, never from the production formatter or a value returned by the first run.
            Assert.Equal(new[] { "JE-f831879958dc50a39874d687d1c07219" }, ledgerTargets);
            ledgerTargets.Clear();
            await Daemon(factory, store, source, firstAt).TickAsync();
            Assert.Equal(1, await CountJournalEntriesAsync(factory));
            Assert.Equal(new[] { "JE-f831879958dc50a39874d687d1c07219" }, ledgerTargets);

            var reopenedFactory = NewFactory();
            var reopenedStore = NewStore(reopenedFactory);
            var reopenedSource = new NodeRecurringScheduleSource(reopenedFactory, new InMemoryRruleExpansionService(), log);
            Assert.Equal(new[] { "generate@2026-01-05", "generate@2026-01-06", "generate@2026-01-07" },
                (await reopenedSource.GetDueTriggersAsync(laterAt)).Select(row => row.Step));
            ledgerTargets.Clear();
            await Daemon(reopenedFactory, reopenedStore, reopenedSource, laterAt).TickAsync();
            Assert.Equal(3, await CountJournalEntriesAsync(reopenedFactory));
            Assert.Equal(new[] { "JE-f831879958dc50a39874d687d1c07219", "JE-9a512e01d8f15785aa8756374d912176", "JE-8980426d04e65f8c9cfa5cd27d1e36d6" }, ledgerTargets);

            foreach (var replayCulture in new[] { cultureName, replayCultureName })
            {
                var replay = new CultureInfo(replayCulture, useUserOverride: false);
                replay.DateTimeFormat.Calendar = replayCulture == "ar-SA" ? new UmAlQuraCalendar() : new ThaiBuddhistCalendar();
                CultureInfo.CurrentCulture = replay;
                Assert.False(CultureInfo.CurrentCulture.DateTimeFormat.Calendar is GregorianCalendar);
                var freshFactory = NewFactory();
                var freshStore = NewStore(freshFactory);
                var freshSource = new NodeRecurringScheduleSource(freshFactory, new InMemoryRruleExpansionService(), log);
                Assert.Equal("generate@2026-01-07", Assert.Single(await freshSource.GetDueTriggersAsync(laterAt)).Step);
                ledgerTargets.Clear();
                await Daemon(freshFactory, freshStore, freshSource, laterAt).TickAsync();
                Assert.Equal(new[] { "JE-8980426d04e65f8c9cfa5cd27d1e36d6" }, ledgerTargets);
                var retained = (await freshStore.LoadAsync(id))!;
                Assert.Equal("generate@2026-01-07", retained.CurrentStep);
                Assert.Equal(seeded.StateJson, retained.StateJson);
                Assert.Equal("v1", retained.DefinitionVersion);
                Assert.Equal(WorkflowStatus.Running, retained.Status);
                Assert.Equal(0, retained.Iteration);
                Assert.Equal(createdAt, retained.CreatedAt);
                Assert.Equal(laterAt, retained.UpdatedAt);
                await using var context = await freshFactory.CreateDbContextAsync();
                var entries = await context.Set<JournalEntry>().OrderBy(row => row.EntryDate).ToArrayAsync();
                Assert.Equal(new DateOnly[] { new(2026, 1, 5), new(2026, 1, 6), new(2026, 1, 7) }, entries.Select(row => row.EntryDate));
                Assert.Equal(new[] { "JE-f831879958dc50a39874d687d1c07219", "JE-9a512e01d8f15785aa8756374d912176", "JE-8980426d04e65f8c9cfa5cd27d1e36d6" }, entries.Select(row => row.Id.Value));
                Assert.Equal(new[] { "recurring:SCHED-ISO-CULTURE:2026-01-05", "recurring:SCHED-ISO-CULTURE:2026-01-06", "recurring:SCHED-ISO-CULTURE:2026-01-07" }, entries.Select(row => row.SourceReference));
                Assert.All(entries, row =>
                {
                    Assert.Equal(LocalTenantId, row.TenantId);
                    Assert.Equal(JournalEntryStatus.Posted, row.Status);
                    Assert.Equal(250m, row.Lines.Sum(line => line.Debit));
                    Assert.Equal(250m, row.Lines.Sum(line => line.Credit));
                });
                Assert.Equal(new[] { "generate@2023-01-01", "generate@2026-01-05", "generate@2026-01-06", "generate@2026-01-07" },
                    await context.Set<WorkflowStepIdempotencyRecord>().Where(row => row.InstanceId == id)
                        .OrderBy(row => row.Step).Select(row => row.Step).ToArrayAsync());
                var events = await context.Set<WorkflowEventRecord>().Where(row => row.InstanceId == id && row.EventType == "OccurrenceGenerated")
                    .OrderBy(row => row.Step).ToArrayAsync();
                Assert.Equal(new[] { "generate@2026-01-05", "generate@2026-01-06", "generate@2026-01-07" }, events.Select(row => row.Step));
                Assert.Equal(new[] { "2026-01-05", "2026-01-06", "2026-01-07" }, events.Select(row =>
                {
                    using var data = JsonDocument.Parse(row.DataJson);
                    Assert.True(data.RootElement.GetProperty("generated").GetBoolean());
                    return data.RootElement.GetProperty("occurrenceDate").GetString();
                }));
                Assert.Empty(log.Entries);
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = priorCulture;
        }
    }

    [Theory]
    [InlineData("FREQ=DAILY", "2026-01-05", "2026-01-07", true)]
    [InlineData("FREQ=WEEKLY;INTERVAL=2;BYDAY=MO", "2026-01-05", "2026-01-14", true)]
    [InlineData("FREQ=DAILY;COUNT=3", "2026-01-05", "2026-01-07", false)]
    public async Task Admitted_definition_uses_last_advance_as_window_without_resetting_old_DTSTART_phase_or_COUNT(
        string rule, string lastAdvance, string asOf, bool hasDue)
    {
        var factory = NewFactory();
        var store = NewStore(factory);
        var anchor = new DateTimeOffset(2020, 1, 6, 0, 0, 0, TimeSpan.Zero); // Monday.
        var advanced = DateTimeOffset.Parse(lastAdvance + "T00:00:00Z", CultureInfo.InvariantCulture);
        var at = DateTimeOffset.Parse(asOf + "T00:00:00Z", CultureInfo.InvariantCulture);
        var definitions = await NewPublishedSchedulesAsync("cursor-definition", anchor, rule);
        await CreateWaitingInstanceAsync(store, "definition-cursor", "cursor-definition", anchor, advanced);
        var log = new ScheduleLog();
        var source = new NodeRecurringScheduleSource(factory, new InMemoryRruleExpansionService(), definitions, log);
        var due = await source.GetDueTriggersAsync(at);
        if (hasDue)
        {
            var trigger = Assert.Single(due);
            Assert.Equal("definition-cursor", trigger.InstanceId);
            Assert.Equal("waiting", trigger.Step);
        }
        else Assert.Empty(due);
        Assert.Empty(log.Entries);
        Assert.Equal(0, await CountJournalEntriesAsync(factory));

        // The source's real recurrence inputs are separately pinned by literal date oracles. In
        // particular Jan5 is off-phase (313 weeks after DTSTART); rebasing would incorrectly emit it.
        var expansion = new InMemoryRruleExpansionService();
        var dates = expansion.ExpandOccurrences(rule, new DateOnly(2020, 1, 6), DateOnly.FromDateTime(at.UtcDateTime),
            0, 0, DateOnly.FromDateTime(advanced.UtcDateTime), "UTC");
        if (rule.StartsWith("FREQ=WEEKLY", StringComparison.Ordinal))
        {
            Assert.Equal(new DateOnly[] { new(2026, 1, 12) }, dates);
            Assert.Empty(await source.GetDueTriggersAsync(new DateTimeOffset(2026, 1, 11, 0, 0, 0, TimeSpan.Zero)));
        }
        else if (hasDue) Assert.Equal(new DateOnly[] { new(2026, 1, 5), new(2026, 1, 6), new(2026, 1, 7) }, dates);
        else Assert.Empty(dates);
        var retained = (await store.LoadAsync("definition-cursor"))!;
        Assert.Equal(anchor, retained.CreatedAt);
        Assert.Equal(advanced, retained.UpdatedAt);
        Assert.Equal("waiting", retained.CurrentStep);
    }

    [Fact]
    public async Task Later_lawful_binding_cap_discards_earlier_candidate_for_only_that_admitted_instance()
    {
        var factory = NewFactory();
        var store = NewStore(factory);
        var anchor = new DateTimeOffset(2023, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var at = new DateTimeOffset(2026, 1, 7, 0, 0, 0, TimeSpan.Zero);
        // Both rules really pass register/load admission. The sparse first binding has due dates;
        // the second lawful DAILY binding exceeds1000 only for this instance's unadvanced backlog.
        var definitions = await NewPublishedSchedulesAsync("multi-binding", anchor,
            "FREQ=MONTHLY;BYMONTHDAY=1", "FREQ=DAILY");
        await CreateWaitingInstanceAsync(store, "definition-with-backlog", "multi-binding", anchor, null);
        await SeedScheduleAsync(factory, "SCHED-HEALTHY", "FREQ=MONTHLY;BYMONTHDAY=1", new DateOnly(2026, 1, 1), 250m);
        await NewInstantiation(store, factory).SyncRecurringGenerationProcessesAsync(LocalTenantId, at);
        var before = (await store.LoadAsync("definition-with-backlog"))!;
        var log = new ScheduleLog();
        var source = new NodeRecurringScheduleSource(factory, new InMemoryRruleExpansionService(), definitions, log);
        var due = await source.GetDueTriggersAsync(at);
        Assert.Equal("recurring-generation:SCHED-HEALTHY", Assert.Single(due).InstanceId);
        Assert.IsType<global::Harborline.Foundation.Scheduling.RruleExpansionCapExceededException>(Assert.Single(log.Entries).Error);
        Assert.Contains("definition-with-backlog", log.Entries[0].Message, StringComparison.Ordinal);
        await NewRecurringDaemon(factory, store, source, at).TickAsync();
        Assert.Equal(1, await CountJournalEntriesAsync(factory));
        var after = (await NewStore(NewFactory()).LoadAsync("definition-with-backlog"))!;
        Assert.Equal(before.CurrentStep, after.CurrentStep);
        Assert.Equal(before.StateJson, after.StateJson);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        await using var context = await factory.CreateDbContextAsync();
        Assert.Equal(0, await context.Set<WorkflowStepIdempotencyRecord>().CountAsync(row => row.InstanceId == "definition-with-backlog"));
        Assert.Equal(0, await context.Set<WorkflowEventRecord>().CountAsync(row => row.InstanceId == "definition-with-backlog"));
    }

    [Fact]
    public async Task Definition_read_operational_failure_and_caller_cancellation_are_not_expansion_refusals()
    {
        var factory = NewFactory();
        var store = NewStore(factory);
        var at = new DateTimeOffset(2026, 1, 7, 0, 0, 0, TimeSpan.Zero);
        await CreateWaitingInstanceAsync(store, "read-failure", "read-failure", at, null);
        var expected = new NotSupportedException("literal definition-store failure, not recurrence refusal");
        var log = new ScheduleLog();
        var source = new NodeRecurringScheduleSource(factory, new InMemoryRruleExpansionService(),
            new FailingDefinitionRead(expected), log);
        var actual = await Assert.ThrowsAsync<NotSupportedException>(() => source.GetDueTriggersAsync(at));
        Assert.Same(expected, actual);
        Assert.Empty(log.Entries);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.GetDueTriggersAsync(at, cancellation.Token));
        Assert.Empty(log.Entries);
    }

    [Fact]
    public async Task Typed_schedule_keeps_exhausted_original_COUNT_when_its_durable_cursor_is_years_later()
    {
        var factory = NewFactory();
        var store = NewStore(factory);
        await SeedScheduleAsync(factory, "SCHED-TYPED-COUNT", "FREQ=DAILY;COUNT=3", new DateOnly(2023, 1, 1), 250m);
        Assert.Equal(1, await NewInstantiation(store, factory).SyncRecurringGenerationProcessesAsync(LocalTenantId,
            new DateTimeOffset(2023, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        const string id = "recurring-generation:SCHED-TYPED-COUNT";
        await store.AdvanceAsync(new WorkflowStepKey(id, 0, "generate@2023-01-01"), null, "{}", "Advanced", "{}",
            "generate@2026-01-05", WorkflowStatus.Running, new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero));

        // The real setup advance has already written its event and idempotency receipt. The
        // exhausted 2023 COUNT must add nothing beyond this baseline, even after reopening.
        var before = (await store.LoadAsync(id))!;
        Assert.Contains("\"startsOn\":\"2023-01-01\"", before.StateJson, StringComparison.Ordinal);
        Assert.Equal("generate@2026-01-05", before.CurrentStep);
        Assert.Equal(0, await CountJournalEntriesAsync(factory));
        await using (var context = await factory.CreateDbContextAsync())
        {
            Assert.Equal(1, await context.Set<WorkflowStepIdempotencyRecord>().CountAsync(row => row.InstanceId == id));
            Assert.Equal(1, await context.Set<WorkflowEventRecord>().CountAsync(row => row.InstanceId == id));
        }
        var at = new DateTimeOffset(2026, 1, 7, 0, 0, 0, TimeSpan.Zero);
        var log = new ScheduleLog();
        var source = new NodeRecurringScheduleSource(factory, new InMemoryRruleExpansionService(), log);
        // Resetting DTSTART to the cursor would incorrectly produce Jan5/6/7 instead of zero.
        Assert.Empty(await source.GetDueTriggersAsync(at));
        Assert.Empty(log.Entries);
        await NewRecurringDaemon(factory, store, source, at).TickAsync();
        Assert.Equal(0, await CountJournalEntriesAsync(factory));

        var reopenedFactory = NewFactory();
        var reopenedStore = NewStore(reopenedFactory);
        var reopenedSource = new NodeRecurringScheduleSource(reopenedFactory, new InMemoryRruleExpansionService(), log);
        Assert.Empty(await reopenedSource.GetDueTriggersAsync(at));
        await NewRecurringDaemon(reopenedFactory, reopenedStore, reopenedSource, at).TickAsync();
        Assert.Empty(log.Entries);
        Assert.Equal(0, await CountJournalEntriesAsync(reopenedFactory));
        var after = (await reopenedStore.LoadAsync(id))!;
        Assert.Equal(WorkflowStatus.Running, after.Status);
        Assert.Equal(before.CurrentStep, after.CurrentStep);
        Assert.Equal(before.Iteration, after.Iteration);
        Assert.Equal(before.StateJson, after.StateJson);
        Assert.Equal(before.DefinitionVersion, after.DefinitionVersion);
        Assert.Equal(before.CreatedAt, after.CreatedAt);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        await using var fresh = await reopenedFactory.CreateDbContextAsync();
        Assert.Equal(1, await fresh.Set<WorkflowStepIdempotencyRecord>().CountAsync(row => row.InstanceId == id));
        Assert.Equal(1, await fresh.Set<WorkflowEventRecord>().CountAsync(row => row.InstanceId == id));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public async Task Persisted_blank_typed_rule_is_skipped_while_healthy_monthly_dispatch_survives_reopen(string rule)
    {
        var factory = NewFactory();
        var store = NewStore(factory);
        var at = new DateTimeOffset(2026, 1, 7, 0, 0, 0, TimeSpan.Zero);
        const string id = "recurring-generation:SCHED-BLANK";
        // This is malformed legacy persisted state, not a new admitted blank schedule. The
        // source must skip it before invoking the strict producer, without widening its catches.
        await store.CreateInstanceAsync(new WorkflowInstanceRecord
        {
            Id = id, TenantId = "local", DefinitionKey = RecurringGenerationSteps.DefinitionKey,
            DefinitionVersion = "v1", CurrentStep = "generate@2026-01-01", Status = WorkflowStatus.Running,
            StateJson = JsonSerializer.Serialize(new
            {
                scheduleId = "SCHED-BLANK", rrule = rule, startsOn = "2026-01-01", timezone = "UTC",
                amount = 250m, debitAccount = "1100", creditAccount = "4000",
            }),
        }, at);
        await SeedScheduleAsync(factory, "SCHED-BLANK-HEALTHY", "FREQ=MONTHLY;BYMONTHDAY=1", new DateOnly(2026, 1, 1), 250m);
        Assert.Equal(1, await NewInstantiation(store, factory).SyncRecurringGenerationProcessesAsync(LocalTenantId, at));
        var before = (await store.LoadAsync(id))!;
        Assert.Equal(0, await CountJournalEntriesAsync(factory));
        await using (var context = await factory.CreateDbContextAsync())
        {
            Assert.Equal(0, await context.Set<WorkflowStepIdempotencyRecord>().CountAsync(row => row.InstanceId == id));
            Assert.Equal(0, await context.Set<WorkflowEventRecord>().CountAsync(row => row.InstanceId == id));
        }
        var log = new ScheduleLog();
        var source = new NodeRecurringScheduleSource(factory, new InMemoryRruleExpansionService(), log);
        var healthy = Assert.Single(await source.GetDueTriggersAsync(at));
        Assert.Equal("recurring-generation:SCHED-BLANK-HEALTHY", healthy.InstanceId);
        Assert.Equal("generate@2026-01-01", healthy.Step);
        Assert.Empty(log.Entries);
        await NewRecurringDaemon(factory, store, source, at).TickAsync();
        Assert.Equal(1, await CountJournalEntriesAsync(factory));

        var reopenedFactory = NewFactory();
        var reopenedStore = NewStore(reopenedFactory);
        var reopenedSource = new NodeRecurringScheduleSource(reopenedFactory, new InMemoryRruleExpansionService(), log);
        var repeated = Assert.Single(await reopenedSource.GetDueTriggersAsync(at));
        Assert.Equal("recurring-generation:SCHED-BLANK-HEALTHY", repeated.InstanceId);
        Assert.Equal("generate@2026-01-01", repeated.Step);
        await NewRecurringDaemon(reopenedFactory, reopenedStore, reopenedSource, at).TickAsync();
        Assert.Equal(1, await CountJournalEntriesAsync(reopenedFactory));
        Assert.Empty(log.Entries);
        var after = (await reopenedStore.LoadAsync(id))!;
        Assert.Equal(WorkflowStatus.Running, after.Status);
        Assert.Equal(before.CurrentStep, after.CurrentStep);
        Assert.Equal(before.Iteration, after.Iteration);
        Assert.Equal(before.StateJson, after.StateJson);
        Assert.Equal(before.DefinitionVersion, after.DefinitionVersion);
        Assert.Equal(before.CreatedAt, after.CreatedAt);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        await using var fresh = await reopenedFactory.CreateDbContextAsync();
        Assert.Equal(0, await fresh.Set<WorkflowStepIdempotencyRecord>().CountAsync(row => row.InstanceId == id));
        Assert.Equal(0, await fresh.Set<WorkflowEventRecord>().CountAsync(row => row.InstanceId == id));
        Assert.Equal(1, await fresh.Set<WorkflowStepIdempotencyRecord>()
            .CountAsync(row => row.InstanceId == "recurring-generation:SCHED-BLANK-HEALTHY"));
        Assert.Equal(1, await fresh.Set<WorkflowEventRecord>()
            .CountAsync(row => row.InstanceId == "recurring-generation:SCHED-BLANK-HEALTHY"));
    }

    [Fact]
    public async Task Stored_definition_admission_refusal_skips_only_that_instance_and_returns_the_healthy_schedule()
    {
        var factory = NewFactory();
        var store = NewStore(factory);
        var anchor = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var at = new DateTimeOffset(2026, 1, 7, 0, 0, 0, TimeSpan.Zero);
        var definitions = await NewStoredInadmissibleScheduleAsync(anchor);
        await CreateWaitingInstanceAsync(store, "definition-refused", "stored-admission-refusal", anchor, null);
        await SeedScheduleAsync(factory, "SCHED-HEALTHY", "FREQ=MONTHLY;BYMONTHDAY=1", new DateOnly(2026, 1, 1), 250m);
        Assert.Equal(1, await NewInstantiation(store, factory).SyncRecurringGenerationProcessesAsync(LocalTenantId, at));
        var before = (await store.LoadAsync("definition-refused"))!;
        long eventsBefore;
        await using (var context = await factory.CreateDbContextAsync())
        {
            eventsBefore = await context.Set<WorkflowEventRecord>()
                .LongCountAsync(row => row.InstanceId == "definition-refused");
            Assert.Equal(0, await context.Set<WorkflowStepIdempotencyRecord>()
                .CountAsync(row => row.InstanceId == "definition-refused"));
        }
        var log = new ScheduleLog();
        var source = new NodeRecurringScheduleSource(factory, new InMemoryRruleExpansionService(), definitions, log);

        var due = await source.GetDueTriggersAsync(at);

        var healthy = Assert.Single(due);
        Assert.Equal(WorkflowTriggerKind.Schedule, healthy.Kind);
        Assert.Equal("recurring-generation:SCHED-HEALTHY", healthy.InstanceId);
        Assert.Equal("generate@2026-01-01", healthy.Step);
        var refusal = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, refusal.Level);
        var admission = Assert.IsType<WorkflowAdmissionException>(refusal.Error);
        Assert.Equal("workflow.admission.schedule_rrule_invalid", Assert.Single(admission.Result.Violations).Code);
        Assert.Contains("definition-refused", refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("EXDATE", refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("FREQ=DAILY", refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("WorkflowDefinition refused at admission", refusal.Message, StringComparison.Ordinal);

        var reopenedFactory = NewFactory();
        var after = (await NewStore(reopenedFactory).LoadAsync("definition-refused"))!;
        Assert.Equal(WorkflowStatus.Running, after.Status);
        Assert.Equal("waiting", after.CurrentStep);
        Assert.Equal(before.CurrentStep, after.CurrentStep);
        Assert.Equal(before.StateJson, after.StateJson);
        Assert.Equal("1.0.0", after.DefinitionVersion);
        Assert.Equal(anchor, after.CreatedAt);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        await using var fresh = await reopenedFactory.CreateDbContextAsync();
        Assert.Equal(0, await fresh.Set<WorkflowStepIdempotencyRecord>()
            .CountAsync(row => row.InstanceId == "definition-refused"));
        Assert.Equal(eventsBefore, await fresh.Set<WorkflowEventRecord>()
            .LongCountAsync(row => row.InstanceId == "definition-refused"));
        Assert.Equal(0, await CountJournalEntriesAsync(reopenedFactory));
    }

    [Fact]
    public async Task Stored_definition_read_cancellation_propagates_from_the_actual_read_boundary()
    {
        var factory = NewFactory();
        var at = new DateTimeOffset(2026, 1, 7, 0, 0, 0, TimeSpan.Zero);
        await CreateWaitingInstanceAsync(NewStore(factory), "cancelled-definition-read", "cancelled-definition-read", at, null);
        using var cancellation = new CancellationTokenSource();
        var expected = new OperationCanceledException("literal definition read cancellation", cancellation.Token);
        var definitions = new BoundaryDefinitionFailure(expected, cancellation.Cancel);
        var log = new ScheduleLog();
        var source = new NodeRecurringScheduleSource(factory, new InMemoryRruleExpansionService(), definitions, log);
        Assert.False(cancellation.IsCancellationRequested);

        var actual = await Assert.ThrowsAsync<OperationCanceledException>(
            () => source.GetDueTriggersAsync(at, cancellation.Token));

        Assert.Same(expected, actual);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(cancellation.Token, actual.CancellationToken);
        Assert.Equal(cancellation.Token, definitions.CallerToken);
        Assert.Equal(1, definitions.ExactReads);
        Assert.Empty(log.Entries);
    }

    [Fact]
    public async Task Stored_definition_read_base_invalid_operation_is_not_an_admission_refusal()
    {
        var factory = NewFactory();
        var at = new DateTimeOffset(2026, 1, 7, 0, 0, 0, TimeSpan.Zero);
        await CreateWaitingInstanceAsync(NewStore(factory), "operational-definition-read", "operational-definition-read", at, null);
        var expected = new InvalidOperationException("literal operational definition read failure");
        var definitions = new BoundaryDefinitionFailure(expected);
        var log = new ScheduleLog();
        var source = new NodeRecurringScheduleSource(factory, new InMemoryRruleExpansionService(), definitions, log);

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => source.GetDueTriggersAsync(at));

        Assert.Same(expected, actual);
        Assert.Equal(1, definitions.ExactReads);
        Assert.Empty(log.Entries);
    }

    [Theory]
    [InlineData(false, "unsupported")]
    [InlineData(false, "malformed")]
    [InlineData(false, "cap")]
    [InlineData(false, "success-due")]
    [InlineData(false, "success-empty")]
    [InlineData(true, "unsupported")]
    [InlineData(true, "malformed")]
    [InlineData(true, "cap")]
    [InlineData(true, "success-due")]
    [InlineData(true, "success-empty")]
    public async Task Cancellation_completion_observes_the_last_expansion_even_after_refusal_or_success(
        bool declarative, string boundary)
    {
        var rule = boundary == "success-empty" ? "FREQ=MONTHLY;BYMONTHDAY=8" : "FREQ=MONTHLY;BYMONTHDAY=1";
        var fixture = await NewCompletionFixtureAsync(declarative, rule);
        using var cancellation = new CancellationTokenSource();
        var failure = CompletionExpansionFault(boundary);
        var expansion = new CompletionBoundaryExpansion(cancellation, failure, cancel: true);
        var log = new ScheduleLog();
        var source = NewCompletionSource(fixture, expansion, log);
        Assert.False(cancellation.IsCancellationRequested);

        // Only the source call can satisfy this cancellation assertion, not a later verification read.
        var actual = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => source.GetDueTriggersAsync(CompletionAt, cancellation.Token));

        Assert.Equal(cancellation.Token, actual.CancellationToken);
        Assert.True(cancellation.IsCancellationRequested);
        AssertCompletionExpansion(expansion, rule, boundary == "success-empty");
        if (failure is null) Assert.Empty(log.Entries);
        else AssertCompletionWarning(log, fixture.Id, failure);
        await AssertCompletionStateUnchangedAsync(fixture);
    }

    [Theory]
    [InlineData("admission")]
    [InlineData("missing")]
    public async Task Cancellation_completion_observes_the_last_exact_read_skip(string boundary)
    {
        var fixture = await NewCompletionFixtureAsync(declarative: true);
        using var cancellation = new CancellationTokenSource();
        var failure = CompletionReadFault(boundary);
        var definitions = new CompletionDefinitionReadBoundary(cancellation, failure, cancel: true);
        var expansion = new CompletionBoundaryExpansion(cancellation, failure: null, cancel: false);
        var log = new ScheduleLog();
        var source = new NodeRecurringScheduleSource(fixture.Factory, expansion, definitions, log);
        Assert.False(cancellation.IsCancellationRequested);

        var actual = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => source.GetDueTriggersAsync(CompletionAt, cancellation.Token));

        Assert.Equal(cancellation.Token, actual.CancellationToken);
        Assert.True(cancellation.IsCancellationRequested);
        AssertCompletionRead(definitions, cancellation.Token);
        Assert.Equal(0, expansion.Calls);
        if (boundary == "admission") AssertCompletionWarning(log, fixture.Id, failure);
        else Assert.Empty(log.Entries);
        await AssertCompletionStateUnchangedAsync(fixture);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_completion_preserves_the_original_unknown_expansion_fault(bool declarative)
    {
        var fixture = await NewCompletionFixtureAsync(declarative);
        using var cancellation = new CancellationTokenSource();
        var expected = new InvalidOperationException("literal unknown expansion fault");
        var expansion = new CompletionBoundaryExpansion(cancellation, expected, cancel: true);
        var log = new ScheduleLog();
        var source = NewCompletionSource(fixture, expansion, log);
        Assert.False(cancellation.IsCancellationRequested);

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.GetDueTriggersAsync(CompletionAt, cancellation.Token));

        Assert.Same(expected, actual);
        Assert.True(cancellation.IsCancellationRequested);
        AssertCompletionExpansion(expansion, "FREQ=MONTHLY;BYMONTHDAY=1", empty: false);
        Assert.Empty(log.Entries);
        await AssertCompletionStateUnchangedAsync(fixture);
    }

    [Fact]
    public async Task Cancellation_completion_preserves_the_original_unknown_exact_read_fault()
    {
        var fixture = await NewCompletionFixtureAsync(declarative: true);
        using var cancellation = new CancellationTokenSource();
        var expected = new InvalidOperationException("literal unknown definition read fault");
        var definitions = new CompletionDefinitionReadBoundary(cancellation, expected, cancel: true);
        var expansion = new CompletionBoundaryExpansion(cancellation, failure: null, cancel: false);
        var log = new ScheduleLog();
        var source = new NodeRecurringScheduleSource(fixture.Factory, expansion, definitions, log);
        Assert.False(cancellation.IsCancellationRequested);

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.GetDueTriggersAsync(CompletionAt, cancellation.Token));

        Assert.Same(expected, actual);
        Assert.True(cancellation.IsCancellationRequested);
        AssertCompletionRead(definitions, cancellation.Token);
        Assert.Equal(0, expansion.Calls);
        Assert.Empty(log.Entries);
        await AssertCompletionStateUnchangedAsync(fixture);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_completion_preserves_an_already_thrown_expansion_cancellation(bool declarative)
    {
        var fixture = await NewCompletionFixtureAsync(declarative);
        using var cancellation = new CancellationTokenSource();
        var expected = new OperationCanceledException("literal expansion dependency cancellation", cancellation.Token);
        var expansion = new CompletionBoundaryExpansion(cancellation, expected, cancel: true);
        var log = new ScheduleLog();
        var source = NewCompletionSource(fixture, expansion, log);
        Assert.False(cancellation.IsCancellationRequested);

        var actual = await Assert.ThrowsAsync<OperationCanceledException>(
            () => source.GetDueTriggersAsync(CompletionAt, cancellation.Token));

        Assert.Same(expected, actual);
        Assert.Equal(cancellation.Token, actual.CancellationToken);
        Assert.True(cancellation.IsCancellationRequested);
        AssertCompletionExpansion(expansion, "FREQ=MONTHLY;BYMONTHDAY=1", empty: false);
        Assert.Empty(log.Entries);
        await AssertCompletionStateUnchangedAsync(fixture);
    }

    [Theory]
    [InlineData("admission")]
    [InlineData("missing")]
    public async Task Cancellation_completion_keeps_uncancelled_exact_read_refusal_and_missing_behavior(string boundary)
    {
        var fixture = await NewCompletionFixtureAsync(declarative: true);
        using var cancellation = new CancellationTokenSource();
        var failure = CompletionReadFault(boundary);
        var definitions = new CompletionDefinitionReadBoundary(cancellation, failure, cancel: false);
        var expansion = new CompletionBoundaryExpansion(cancellation, failure: null, cancel: false);
        var log = new ScheduleLog();
        var source = new NodeRecurringScheduleSource(fixture.Factory, expansion, definitions, log);
        Assert.False(cancellation.IsCancellationRequested);

        var due = await source.GetDueTriggersAsync(CompletionAt, cancellation.Token);

        Assert.Empty(due);
        Assert.False(cancellation.IsCancellationRequested);
        AssertCompletionRead(definitions, cancellation.Token);
        Assert.Equal(0, expansion.Calls);
        if (boundary == "admission") AssertCompletionWarning(log, fixture.Id, failure);
        else Assert.Empty(log.Entries);
        await AssertCompletionStateUnchangedAsync(fixture);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_completion_keeps_a_real_uncancelled_monthly_schedule_due(bool declarative)
    {
        var fixture = await NewCompletionFixtureAsync(declarative);
        using var cancellation = new CancellationTokenSource();
        var expansion = new CompletionBoundaryExpansion(cancellation, failure: null, cancel: false);
        var log = new ScheduleLog();
        var source = NewCompletionSource(fixture, expansion, log);
        Assert.False(cancellation.IsCancellationRequested);

        var due = await source.GetDueTriggersAsync(CompletionAt, cancellation.Token);

        var trigger = Assert.Single(due);
        Assert.Equal(WorkflowTriggerKind.Schedule, trigger.Kind);
        Assert.Equal(fixture.Id, trigger.InstanceId);
        Assert.Equal(declarative ? "waiting" : "generate@2026-01-01", trigger.Step);
        Assert.False(cancellation.IsCancellationRequested);
        AssertCompletionExpansion(expansion, "FREQ=MONTHLY;BYMONTHDAY=1", empty: false);
        Assert.Empty(log.Entries);
        await AssertCompletionStateUnchangedAsync(fixture);
    }

    [Fact]
    public async Task Cancellation_completion_keeps_an_uncancelled_empty_store_empty()
    {
        var factory = NewFactory();
        using var cancellation = new CancellationTokenSource();
        var expansion = new CompletionBoundaryExpansion(cancellation, failure: null, cancel: true);
        var definitions = new CompletionDefinitionReadBoundary(cancellation,
            new InvalidOperationException("empty source must not read a definition"), cancel: true);
        var log = new ScheduleLog();
        var source = new NodeRecurringScheduleSource(factory, expansion, definitions, log);
        Assert.False(cancellation.IsCancellationRequested);

        var due = await source.GetDueTriggersAsync(CompletionAt, cancellation.Token);

        Assert.Empty(due);
        Assert.False(cancellation.IsCancellationRequested);
        Assert.Equal(0, expansion.Calls);
        Assert.Equal(0, definitions.ExactReads);
        Assert.Equal(0, definitions.CurrentReads);
        Assert.Empty(log.Entries);
        await using var fresh = await NewFactory().CreateDbContextAsync();
        Assert.Equal(0, await fresh.Set<WorkflowInstanceRecord>().CountAsync());
        Assert.Equal(0, await fresh.Set<WorkflowEventRecord>().CountAsync());
        Assert.Equal(0, await fresh.Set<WorkflowStepIdempotencyRecord>().CountAsync());
        Assert.Equal(0, await fresh.Set<JournalEntry>().CountAsync());
    }

    [Fact]
    public async Task Cancellation_completion_keeps_precancelled_callers_out_of_the_expansion_seam()
    {
        var fixture = await NewCompletionFixtureAsync(declarative: false);
        using var cancellation = new CancellationTokenSource();
        var expansion = new CompletionBoundaryExpansion(cancellation, failure: null, cancel: false);
        var log = new ScheduleLog();
        var source = NewCompletionSource(fixture, expansion, log);
        cancellation.Cancel();

        var actual = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => source.GetDueTriggersAsync(CompletionAt, cancellation.Token));

        Assert.Equal(cancellation.Token, actual.CancellationToken);
        Assert.Equal(0, expansion.Calls);
        Assert.Empty(log.Entries);
        await AssertCompletionStateUnchangedAsync(fixture);
    }

    private static readonly DateTimeOffset CompletionAnchor = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CompletionAt = new(2026, 1, 7, 0, 0, 0, TimeSpan.Zero);

    private sealed record CompletionSnapshot(WorkflowInstanceRecord Instance, string InstanceJson, string EventsJson);
    private sealed record CompletionFixture(
        IDbContextFactory<LocalNodeDbContext> Factory,
        IWorkflowDefinitionExecutionStore? Definitions,
        string Id,
        CompletionSnapshot Before);

    private async Task<CompletionFixture> NewCompletionFixtureAsync(
        bool declarative, string rule = "FREQ=MONTHLY;BYMONTHDAY=1")
    {
        var factory = NewFactory();
        var store = NewStore(factory);
        IWorkflowDefinitionExecutionStore? definitions = null;
        string id;
        if (declarative)
        {
            definitions = await NewPublishedSchedulesAsync("completion-definition", CompletionAnchor, rule);
            id = "completion-declarative";
            await CreateWaitingInstanceAsync(store, id, "completion-definition", CompletionAnchor, null);
        }
        else
        {
            await SeedScheduleAsync(factory, "SCHED-COMPLETION", rule, new DateOnly(2026, 1, 1), 250m);
            Assert.Equal(1, await NewInstantiation(store, factory)
                .SyncRecurringGenerationProcessesAsync(LocalTenantId, CompletionAt));
            id = "recurring-generation:SCHED-COMPLETION";
        }

        var before = Assert.IsType<WorkflowInstanceRecord>(await store.LoadAsync(id));
        Assert.Equal(WorkflowStatus.Running, before.Status);
        Assert.Equal(0, before.Iteration);
        await using var context = await factory.CreateDbContextAsync();
        Assert.Equal(1, await context.Set<WorkflowInstanceRecord>().CountAsync());
        Assert.Equal(0, await context.Set<WorkflowStepIdempotencyRecord>().CountAsync());
        Assert.Equal(0, await context.Set<JournalEntry>().CountAsync());
        var events = await context.Set<WorkflowEventRecord>().AsNoTracking().OrderBy(row => row.Id).ToListAsync();
        return new(factory, definitions, id,
            new(before, JsonSerializer.Serialize(before), JsonSerializer.Serialize(events)));
    }

    private static NodeRecurringScheduleSource NewCompletionSource(
        CompletionFixture fixture, IRruleExpansionService expansion, ScheduleLog log)
        => fixture.Definitions is null
            ? new(fixture.Factory, expansion, log)
            : new(fixture.Factory, expansion, fixture.Definitions, log);

    private async Task AssertCompletionStateUnchangedAsync(CompletionFixture fixture)
    {
        // These fresh reads deliberately do not use the cancelled source token.
        var factory = NewFactory();
        var after = Assert.IsType<WorkflowInstanceRecord>(await NewStore(factory).LoadAsync(fixture.Id));
        var before = fixture.Before.Instance;
        Assert.Equal(fixture.Id, after.Id);
        Assert.Equal("local", after.TenantId);
        Assert.Equal(before.DefinitionKey, after.DefinitionKey);
        Assert.Equal(before.DefinitionVersion, after.DefinitionVersion);
        Assert.Equal(before.CurrentStep, after.CurrentStep);
        Assert.Equal(0, after.Iteration);
        Assert.Equal(WorkflowStatus.Running, after.Status);
        Assert.Equal(before.StateJson, after.StateJson);
        Assert.Equal(before.CreatedAt, after.CreatedAt);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        Assert.Equal(fixture.Before.InstanceJson, JsonSerializer.Serialize(after));
        await using var fresh = await factory.CreateDbContextAsync();
        Assert.Equal(1, await fresh.Set<WorkflowInstanceRecord>().CountAsync());
        var events = await fresh.Set<WorkflowEventRecord>().AsNoTracking().OrderBy(row => row.Id).ToListAsync();
        Assert.Equal(fixture.Before.EventsJson, JsonSerializer.Serialize(events));
        Assert.Equal(0, await fresh.Set<WorkflowStepIdempotencyRecord>().CountAsync());
        Assert.Equal(0, await fresh.Set<JournalEntry>().CountAsync());
    }

    private static Exception? CompletionExpansionFault(string boundary)
    {
        if (boundary == "unsupported") return new NotSupportedException("literal unsupported expansion refusal");
        if (boundary == "malformed") return new FormatException("literal malformed expansion refusal");
        if (boundary is "success-due" or "success-empty") return null;
        if (boundary != "cap") throw new InvalidOperationException("Unexpected completion fault fixture.");

        var cap = Assert.Throws<global::Harborline.Foundation.Scheduling.RruleExpansionCapExceededException>(() =>
        {
            _ = new InMemoryRruleExpansionService().ExpandOccurrences("FREQ=DAILY",
                new DateOnly(2023, 1, 1), new DateOnly(2026, 1, 7), 0, 0, new DateOnly(2023, 1, 1), "UTC");
        });
        Assert.Equal(1000, cap.CandidateLimit);
        Assert.Equal(1001, cap.CandidatesExamined);
        return cap;
    }

    private static Exception CompletionReadFault(string boundary)
        => boundary switch
        {
            "admission" => new WorkflowAdmissionException(new WorkflowAdmissionResult
            {
                Violations = [new("workflow.admission.schedule_rrule_invalid", "literal refused read", "due")],
            }),
            "missing" => new WorkflowDefinitionNotFoundException("completion-definition", "1.0.0", "local"),
            _ => throw new InvalidOperationException("Unexpected completion read fixture."),
        };

    private static void AssertCompletionWarning(ScheduleLog log, string instanceId, Exception expected)
    {
        var warning = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Same(expected, warning.Error);
        Assert.Equal(
            $"Workflow schedule expansion refused for instance {instanceId}; skipping this instance for this tick.",
            warning.Message);
        Assert.DoesNotContain("FREQ=", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("literal", warning.Message, StringComparison.Ordinal);
    }

    private sealed record CompletionExpansionCall(
        string Rrule, DateOnly Start, DateOnly? End, int LookaheadDays, int LeadDays, DateOnly Today, string Timezone);

    private sealed class CompletionBoundaryExpansion(
        CancellationTokenSource caller, Exception? failure, bool cancel) : IRruleExpansionService
    {
        public int Calls { get; private set; }
        public bool WasUncancelled { get; private set; }
        public CompletionExpansionCall? Request { get; private set; }
        public IReadOnlyList<DateOnly> Dates { get; private set; } = Array.Empty<DateOnly>();

        public IReadOnlyList<DateOnly> ExpandOccurrences(
            string rrule, DateOnly start, DateOnly? end, int lookaheadDays, int leadDays, DateOnly today, string timezone)
        {
            Calls++;
            WasUncancelled = !caller.IsCancellationRequested;
            Request = new(rrule, start, end, lookaheadDays, leadDays, today, timezone);
            // The real strict producer computes first. Only this test seam places cancellation.
            Dates = new InMemoryRruleExpansionService()
                .ExpandOccurrences(rrule, start, end, lookaheadDays, leadDays, today, timezone);
            if (cancel) caller.Cancel();
            if (failure is not null) throw failure;
            return Dates;
        }
    }

    private static void AssertCompletionExpansion(CompletionBoundaryExpansion expansion, string rule, bool empty)
    {
        Assert.Equal(1, expansion.Calls);
        Assert.True(expansion.WasUncancelled);
        var request = Assert.IsType<CompletionExpansionCall>(expansion.Request);
        Assert.Equal(rule, request.Rrule);
        Assert.Equal(new DateOnly(2026, 1, 1), request.Start);
        Assert.Equal<DateOnly?>(new DateOnly(2026, 1, 7), request.End);
        Assert.Equal(0, request.LookaheadDays);
        Assert.Equal(0, request.LeadDays);
        Assert.Equal(new DateOnly(2026, 1, 1), request.Today);
        Assert.Equal("UTC", request.Timezone);
        if (empty) Assert.Empty(expansion.Dates);
        else Assert.Equal(new DateOnly[] { new(2026, 1, 1) }, expansion.Dates);
    }

    private sealed class CompletionDefinitionReadBoundary(
        CancellationTokenSource caller, Exception failure, bool cancel) : IWorkflowDefinitionExecutionStore
    {
        public int ExactReads { get; private set; }
        public int CurrentReads { get; private set; }
        public bool WasUncancelled { get; private set; }
        public DefinitionCoordinates Coordinates { get; private set; }
        public CancellationToken CallerToken { get; private set; }

        public ValueTask<WorkflowDefinitionRecord?> GetAdmittedCurrentPublishedAsync(
            DefinitionAddress address, CancellationToken ct = default)
        {
            CurrentReads++;
            throw new InvalidOperationException("Unexpected current-publication lookup in completion fixture.");
        }

        public ValueTask<WorkflowDefinitionRecord> GetAdmittedAsync(
            DefinitionCoordinates coordinates, CancellationToken ct = default)
        {
            ExactReads++;
            WasUncancelled = !caller.IsCancellationRequested;
            Coordinates = coordinates;
            CallerToken = ct;
            if (cancel) caller.Cancel();
            throw failure;
        }
    }

    private static void AssertCompletionRead(
        CompletionDefinitionReadBoundary definitions, CancellationToken callerToken)
    {
        Assert.Equal(1, definitions.ExactReads);
        Assert.Equal(0, definitions.CurrentReads);
        Assert.True(definitions.WasUncancelled);
        Assert.Equal(new DefinitionCoordinates(LocalTenantId, "completion-definition", "1.0.0"),
            definitions.Coordinates);
        Assert.Equal(callerToken, definitions.CallerToken);
    }


    private static async Task<EntityStoreWorkflowDefinitionStore> NewStoredInadmissibleScheduleAsync(DateTimeOffset at)
    {
        var clock = new FixedClock(at);
        var entities = new InMemoryEntityStore(new InMemoryAssetStorage(), clock);
        var definitions = new EntityStoreWorkflowDefinitionStore(new InMemoryEntityStoreReader(entities), entities,
            new WorkflowAdmissionValidator(), clock);
        var lifecycle = TestAuthorization.WorkflowLifecycle(definitions, TestAuthorization.AllowGate(), TestAuthorization.RoleGate());
        using var authored = JsonDocument.Parse("""
            {
              "tenant": "local", "key": "stored-admission-refusal", "version": "1.0.0", "initialState": "waiting",
              "states": [{"id": "waiting", "kind": "Normal"}, {"id": "done", "kind": "Terminal"}],
              "triggers": [{"id": "due", "kind": "Schedule", "rrule": "FREQ=MONTHLY;BYMONTHDAY=1"}],
              "transitions": [{"id": "advance", "from": "waiting", "on": "due", "to": "done"}],
              "actions": [], "guards": []
            }
            """);
        await lifecycle.RegisterAndPublishAsync(authored.RootElement, TestAuthorization.Write(LocalTenantId, at: at));
        var coordinates = new DefinitionCoordinates(LocalTenantId, "stored-admission-refusal", "1.0.0");
        Assert.Equal(WorkflowDefinitionStatus.Published, (await definitions.GetAdmittedAsync(coordinates)).Status);
        var rows = new List<Entity>();
        await foreach (var row in entities.QueryAsync(new EntityQuery(
            Tenant: global::Harborline.Api.Foundation.MultiTenancy.TenantSelection.Of(LocalTenantId),
            BodyContains: """{"kind":"workflow-definition","key":"stored-admission-refusal","status":"Published"}""")))
        {
            rows.Add(row);
        }
        var stored = Assert.Single(rows);
        var historicalBody = global::System.Text.Json.Nodes.JsonNode.Parse(stored.Body.RootElement.GetRawText())!;
        historicalBody["authored"]!["triggers"]![0]!["rrule"] = "FREQ=DAILY;EXDATE=20260106";
        using var historical = JsonDocument.Parse(historicalBody.ToJsonString());
        // Deliberately reconstruct a stored revision that predates strict recurrence admission.
        await ((IEntityMutationStore)entities).UpdateAsync(stored.Id, historical,
            new UpdateOptions(new ActorId("node:historical-workflow-fixture"), stored.CurrentVersion, at));
        var retained = await definitions.GetAsync(coordinates);
        Assert.Equal(WorkflowDefinitionStatus.Published, retained.Status);
        Assert.Equal("FREQ=DAILY;EXDATE=20260106",
            retained.Authored.GetProperty("triggers")[0].GetProperty("rrule").GetString());
        var refusal = await Assert.ThrowsAsync<WorkflowAdmissionException>(
            async () => await definitions.GetAdmittedAsync(coordinates));
        Assert.Equal("workflow.admission.schedule_rrule_invalid", Assert.Single(refusal.Result.Violations).Code);
        return definitions;
    }

    private sealed class BoundaryDefinitionFailure(Exception failure, Action? beforeFailure = null)
        : IWorkflowDefinitionExecutionStore
    {
        public int ExactReads { get; private set; }
        public CancellationToken CallerToken { get; private set; }

        public ValueTask<WorkflowDefinitionRecord?> GetAdmittedCurrentPublishedAsync(
            DefinitionAddress address, CancellationToken ct = default)
            => throw new InvalidOperationException("Unexpected current-publication lookup.");

        public ValueTask<WorkflowDefinitionRecord> GetAdmittedAsync(
            DefinitionCoordinates coordinates, CancellationToken ct = default)
        {
            ExactReads++;
            CallerToken = ct;
            beforeFailure?.Invoke();
            throw failure;
        }
    }

    private static WorkflowScheduleDaemon NewRecurringDaemon(
        IDbContextFactory<LocalNodeDbContext> factory, IWorkflowStore store,
        IWorkflowScheduleSource source, DateTimeOffset at)
        => new(source, new WorkflowTriggerDispatcher(store, new[] { RecurringHandler(factory) }),
            new FixedClock(at), NullLogger<WorkflowScheduleDaemon>.Instance,
            authorizationGate: TestAuthorization.AllowGate(), store: store);

    private static async Task<EntityStoreWorkflowDefinitionStore> NewPublishedSchedulesAsync(
        string key, DateTimeOffset at, params string[] rules)
    {
        var clock = new FixedClock(at);
        var entities = new InMemoryEntityStore(new InMemoryAssetStorage(), clock);
        var store = new EntityStoreWorkflowDefinitionStore(new InMemoryEntityStoreReader(entities), entities,
            new WorkflowAdmissionValidator(), clock);
        var lifecycle = TestAuthorization.WorkflowLifecycle(store, TestAuthorization.AllowGate(), TestAuthorization.RoleGate());
        using var authored = JsonSerializer.SerializeToDocument(new
        {
            tenant = "local", key, version = "1.0.0", initialState = "start",
            states = new[] { new { id = "start", kind = "Normal" }, new { id = "waiting", kind = "Normal" }, new { id = "done", kind = "Terminal" } },
            triggers = new object[] { new { id = "ready", kind = "Event", eventType = "fixture.ready" } }
                .Concat(rules.Select((rule, index) => (object)new { id = $"schedule-{index}", kind = "Schedule", rrule = rule })).ToArray(),
            transitions = new object[] { new { id = "enter", from = "start", on = "ready", to = "waiting" } }
                .Concat(rules.Select((_, index) => (object)new { id = $"due-{index}", from = "waiting", on = $"schedule-{index}", to = "done" })).ToArray(),
            actions = Array.Empty<object>(), guards = Array.Empty<object>(),
        });
        await lifecycle.RegisterAndPublishAsync(authored.RootElement, TestAuthorization.Write(LocalTenantId, at: at));
        Assert.Equal(WorkflowDefinitionStatus.Published,
            (await store.GetAdmittedAsync(new DefinitionCoordinates(LocalTenantId, key, "1.0.0"))).Status);
        return store;
    }

    private static async Task CreateWaitingInstanceAsync(
        IWorkflowStore store, string id, string definition, DateTimeOffset created, DateTimeOffset? advanced)
    {
        await store.CreateInstanceAsync(new WorkflowInstanceRecord
        {
            Id = id, TenantId = "local", DefinitionKey = definition, DefinitionVersion = "1.0.0",
            CurrentStep = advanced.HasValue ? "start" : "waiting", Status = WorkflowStatus.Running,
        }, created);
        if (advanced is { } at)
        {
            await store.AdvanceAsync(new WorkflowStepKey(id, 0, "start"), null, "{}", "Advanced", "{}",
                "waiting", WorkflowStatus.Running, at);
        }
    }

    private sealed class ScheduleLog : ILogger<NodeRecurringScheduleSource>
    {
        public List<(LogLevel Level, Exception? Error, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, exception, formatter(state, exception)));
    }

    // A failure injection, never a passing/admitted definition or a fake recurrence producer.
    private sealed class FailingDefinitionRead(Exception failure) : IWorkflowDefinitionExecutionStore
    {
        public ValueTask<WorkflowDefinitionRecord?> GetAdmittedCurrentPublishedAsync(DefinitionAddress address, CancellationToken ct = default)
            => throw failure;
        public ValueTask<WorkflowDefinitionRecord> GetAdmittedAsync(DefinitionCoordinates coordinates, CancellationToken ct = default)
            => throw failure;
    }

    private IDbContextFactory<LocalNodeDbContext> NewFactory()
    {
        var services = new ServiceCollection();
        // Financial-ledger (JE) + AR (RecurringInvoiceSchedule) + workflow modules — the schema the
        // instantiation surface + the handler effects touch.
        services.AddSingleton<IHarborlineEntityModule, FinancialLedgerEntityModule>();
        services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialPeriods.Data.FinancialPeriodsEntityModule>();
        services.AddSingleton<IHarborlineEntityModule, ArEntityModule>();
        services.AddSingleton<IHarborlineEntityModule, AuditEventEntityModule>();
        services.AddSingleton<IHarborlineEntityModule, WorkflowEntityModule>();
        services.AddDbContextFactory<LocalNodeDbContext>(o => o.UseSqlite($"Data Source={_dbPath};Pooling=False"));
        return services.BuildServiceProvider().GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
    }

    private NodeEfWorkflowStore NewStore(IDbContextFactory<LocalNodeDbContext> factory)
        => new(factory);

    private static NodeWorkflowInstantiationService NewInstantiation(
        IWorkflowStore store, IDbContextFactory<LocalNodeDbContext> factory)
        => new(store, factory);

    private static IWorkflowStepHandler InvoiceHandler() => WorkflowJoinTestComposition.CreateInvoiceHandler();

    private static IWorkflowStepHandler RecurringHandler(IDbContextFactory<LocalNodeDbContext> factory) =>
        WorkflowJoinTestComposition.CreateRecurringHandler(factory);

    private async Task<int> CountJournalEntriesAsync(IDbContextFactory<LocalNodeDbContext> factory)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        return await ctx.Set<JournalEntry>().CountAsync(j => j.TenantId == LocalTenantId);
    }

    private static async Task<string> StartInvoiceApprovalAsync(
        IDbContextFactory<LocalNodeDbContext> factory,
        NodeWorkflowInstantiationService instantiation,
        string invoiceId,
        decimal amount,
        string memo)
    {
        await WorkflowJoinTestComposition.SeedDraftInvoiceAsync(factory, LocalTenantId, invoiceId, amount);
        return await instantiation.StartInvoiceApprovalAsync(
            LocalTenantId, invoiceId, amount, "1100", "4000", memo);
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
        var instanceId = await StartInvoiceApprovalAsync(
            factory, instantiation, "INV-UNDER", 1000m, "under-threshold issue");

        var pinned = await store.LoadAsync(instanceId);
        Assert.Equal(NodeWorkflowDefinitions.InvoiceApprovalV1Version, pinned!.DefinitionVersion);

        // Drive the decide trigger → under threshold → engine auto-posts.
        var result = await WorkflowJoinTestComposition.DispatchAsync(store, dispatcher,
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
        var overId = await StartInvoiceApprovalAsync(
            factory, instantiation, "INV-OVER", 7500m, "over-threshold issue");
        Assert.Equal(WorkflowDispatchResult.Parked,
            await WorkflowJoinTestComposition.DispatchAsync(store, dispatcher, WorkflowTrigger.For(WorkflowTriggerKind.Event, overId, InvoiceApprovalSteps.Decide)));
        Assert.Equal(0, await CountJournalEntriesAsync(factory));

        // Approve → advance to the CP post with the effect → EXACTLY ONE JE.
        Assert.Equal(WorkflowDispatchResult.Advanced,
            await WorkflowJoinTestComposition.DispatchAsync(store, dispatcher, WorkflowTrigger.For(
                WorkflowTriggerKind.HumanAction, overId, InvoiceApprovalSteps.Approve, "{\"decision\":\"approve\"}")));
        Assert.Equal(1, await CountJournalEntriesAsync(factory));

        // A redelivered approve is a no-op (instance is Completed) — still EXACTLY ONE JE.
        Assert.Equal(WorkflowDispatchResult.Terminal,
            await WorkflowJoinTestComposition.DispatchAsync(store, dispatcher, WorkflowTrigger.For(
                WorkflowTriggerKind.HumanAction, overId, InvoiceApprovalSteps.Approve, "{\"decision\":\"approve\"}")));
        Assert.Equal(1, await CountJournalEntriesAsync(factory));

        // A SEPARATE over-threshold invoice that is REJECTED posts NO JE → total stays at 1.
        var rejId = await StartInvoiceApprovalAsync(factory, instantiation, "INV-REJECT", 9000m, "reject me");
        await WorkflowJoinTestComposition.DispatchAsync(store, dispatcher, WorkflowTrigger.For(WorkflowTriggerKind.Event, rejId, InvoiceApprovalSteps.Decide));
        Assert.Equal(WorkflowDispatchResult.Advanced,
            await WorkflowJoinTestComposition.DispatchAsync(store, dispatcher, WorkflowTrigger.For(
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

        var first = await StartInvoiceApprovalAsync(factory, instantiation, "INV-DUP", 1000m, "m");
        var second = await StartInvoiceApprovalAsync(factory, instantiation, "INV-DUP", 1000m, "m");
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
        var dispatcher = new WorkflowTriggerDispatcher(store, new[] { RecurringHandler(factory) });

        var fixedClock = new FixedClock(new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero));
        var daemon = new WorkflowScheduleDaemon(source, dispatcher, fixedClock, NullLogger<WorkflowScheduleDaemon>.Instance,
            authorizationGate: Harborline.Api.LocalNodeHost.Tests.Authorization.TestAuthorization.AllowGate(), store: store);

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
            new WorkflowTriggerDispatcher(store, new[] { RecurringHandler(factory) }),
            fixedClock, NullLogger<WorkflowScheduleDaemon>.Instance,
            authorizationGate: Harborline.Api.LocalNodeHost.Tests.Authorization.TestAuthorization.AllowGate(), store: store);
        await daemon1.TickAsync();
        var afterFirstBoot = await CountJournalEntriesAsync(factory);
        Assert.True(afterFirstBoot > 0);

        // RESTART — a fresh store + dispatcher + daemon over the SAME db (simulating a crash + resume). The
        // deterministic occurrence id + the per-occurrence idempotency key make the re-tick a no-op.
        var factory2 = NewFactory();
        var store2 = NewStore(factory2);
        var daemon2 = new WorkflowScheduleDaemon(
            new NodeRecurringScheduleSource(factory2, rrule),
            new WorkflowTriggerDispatcher(store2, new[] { RecurringHandler(factory2) }),
            fixedClock, NullLogger<WorkflowScheduleDaemon>.Instance,
            authorizationGate: Harborline.Api.LocalNodeHost.Tests.Authorization.TestAuthorization.AllowGate(), store: store2);
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
