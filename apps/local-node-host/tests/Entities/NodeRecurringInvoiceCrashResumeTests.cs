using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Blocks.FinancialAr.Models;
using Harborline.Api.Blocks.FinancialAr.Services;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialLedger.Services;
using Harborline.Api.Blocks.FinancialPeriods.Models;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.Foundation.Scheduling;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Audit;
using Harborline.Api.LocalNodeHost.Data.Financial;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// bug-1337 / ADR 0135 SC1 — crash-resume regression for <see cref="NodeEfRecurringInvoiceService"/>.
///
/// <para>
/// <b>The bug.</b> The prior two-transaction shape committed the issue JE in <c>IssueAsync</c>'s own
/// transaction and the schedule idempotency record (<c>GeneratedInvoices</c> map) in a SEPARATE
/// transaction at loop-end. A crash in that window orphaned the JE; on resume the service minted a FRESH
/// <c>draftId</c> for the missed occurrence, so the re-post's <c>SourceReference</c> (<c>invoice:{newId}</c>)
/// DIFFERED — even the <c>ux_journal_entries_tenant_source_ref</c> unique index could not dedupe it →
/// DOUBLE-POST.
/// </para>
///
/// <para>
/// <b>The fix (both halves, per the ADR 0135 de-risk spike).</b>
/// (a) STABLE key: the draft id (and therefore the JE source reference) is derived deterministically from
///     <c>(scheduleId, occurrenceDate)</c>, so a resume produces the SAME source reference and the unique
///     index is a deterministic backstop.
/// (b) ATOMIC co-commit: the idempotency record is staged onto the JE write's context (via
///     <see cref="NodeRecurringInvoiceWriteEnlister"/> riding <see cref="NodeEfJournalStore"/>'s single
///     <c>SaveChangesAsync</c>), so the JE + the record commit (or roll back) in ONE SQLite transaction.
/// </para>
///
/// <para>
/// These tests build the PRODUCTION node posting stack (the real <see cref="InvoicePostingService"/> over
/// the real <see cref="JournalPostingService"/> + node resolvers + <see cref="NodeEfJournalStore"/>) over an
/// on-disk SQLite <see cref="LocalNodeDbContext"/>, crash-inject at the effect→idempotency window, then
/// RESUME on a fresh store (= a process restart) and assert EXACTLY ONE JE is posted.
/// </para>
/// </summary>
public sealed class NodeRecurringInvoiceCrashResumeTests : IAsyncLifetime
{
    private string _dir = null!;
    private IDbContextFactory<LocalNodeDbContext> _factory = null!;

    private static readonly TenantId LocalTenantId =
        ActiveTeamTenantContext.ProjectTenantId(NodeTestActiveTeam.TestTeamId);

    private static readonly PartyId Actor = new("actor-1");
    private static readonly Harborline.Api.Foundation.Authorization.AuthorizationWriteContext Authority =
        TestAuthorization.Write(
            LocalTenantId,
            Actor.Value,
            new DateTimeOffset(2026, 3, 1, 7, 8, 9, TimeSpan.Zero));
    private static readonly DateOnly OccurrenceDate = new(2026, 3, 1);
    private static readonly RecurringInvoiceScheduleId ScheduleId =
        new("00000000-0000-0000-0000-0000000000a1");

    public async Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "harborline-recurring-crash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var connectionString = $"Data Source={Path.Combine(_dir, "recurring-test.db")};Pooling=False";

        var services = new ServiceCollection();
        // The SAME entity-module set the production node composes: Invoice + schedule via ArEntityModule;
        // JournalEntry + GLAccount via FinancialLedgerEntityModule.
        services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialAr.Data.ArEntityModule>();
        services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialLedger.Data.FinancialLedgerEntityModule>();
        services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialPeriods.Data.FinancialPeriodsEntityModule>();
        services.AddSingleton<IHarborlineEntityModule, AuditEventEntityModule>();
        services.AddDbContextFactory<LocalNodeDbContext>(opt => opt.UseSqlite(connectionString));
        var provider = services.BuildServiceProvider();

        _factory = provider.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            await ctx.Database.EnsureCreatedAsync();
        }

        await SeedAccountAsync("1100", "Accounts Receivable", GLAccountType.Asset, AccountSubtype.AccountsReceivable);
        await SeedAccountAsync("4000", "Service Income", GLAccountType.Revenue, AccountSubtype.OperatingIncome);
        await SeedOpenPeriodAsync(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));
        await SeedScheduleAsync();
    }

    public async Task DisposeAsync()
    {
        await Task.CompletedTask;
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    [Theory]
    [InlineData("unsupported")]
    [InlineData("malformed")]
    [InlineData("cap")]
    public async Task Strict_recurrence_refusal_precedes_invoice_JE_and_generation_cursor_writes(string failure)
    {
        var id = new RecurringInvoiceScheduleId("00000000-0000-0000-0000-0000000000b1");
        var rule = failure switch
        {
            "unsupported" => "FREQ=DAILY;EXDATE=20260106",
            "malformed" => "FREQ=DAILY;INTERVAL=0",
            "cap" => "FREQ=DAILY",
            _ => throw new InvalidOperationException("Unknown literal strict recurrence fixture."),
        };
        var schedule = RecurringInvoiceSchedule.Create(
            tenantId: LocalTenantId, chartId: new ChartOfAccountsId("CH-1"), customerId: new PartyId("customer-1"),
            arAccountId: new GLAccountId("1100"), recurrenceRule: rule, startsOn: new DateOnly(2026, 1, 1),
            timezone: "UTC", lineTemplates: new[] { new RecurringInvoiceLineTemplate("Strict fixture", 1m, 250m, new GLAccountId("4000")) },
            endsOn: null, lookaheadHorizonDays: failure == "cap" ? 1000 : 2, generateLeadDays: 0, id: id);
        await using (var context = await _factory.CreateDbContextAsync())
        {
            context.Set<RecurringInvoiceSchedule>().Add(schedule);
            await context.SaveChangesAsync();
            Assert.Equal(0, await context.Set<Invoice>().IgnoreQueryFilters().CountAsync());
        }
        var service = BuildService(new NodeRecurringInvoiceWriteEnlister());
        var asOf = new DateOnly(2026, 1, 1); // Keep the full1001-date window; do not filter to the far endpoint.
        var result = await service.GenerateDueInvoicesAsync(id, asOf, Authority);
        Assert.Equal(ScheduleGenerationOutcome.Failed, result.Outcome);
        Assert.Empty(result.Invoices);
        Assert.Equal(failure switch
        {
            "cap" => "RecurrenceRule exceeds the supported expansion limit for the requested range.",
            "malformed" => "RecurrenceRule has an invalid INTERVAL value.",
            _ => "RecurrenceRule contains an unsupported component.",
        }, result.Error);
        Assert.InRange(result.Error!.Length, 1, 160);

        await using var fresh = await _factory.CreateDbContextAsync();
        Assert.Equal(0, await fresh.Set<Invoice>().IgnoreQueryFilters().CountAsync());
        Assert.Equal(0, await CountJournalEntriesAsync());
        var retained = await fresh.Set<RecurringInvoiceSchedule>().IgnoreQueryFilters().AsNoTracking().SingleAsync(row => row.Id == id);
        Assert.Empty(retained.GeneratedInvoices);
        Assert.Null(retained.LastGeneratedAtUtc);
        Assert.Equal(RecurringScheduleStatus.Active, retained.Status);
        Assert.Equal(rule, retained.RecurrenceRule);
        Assert.Equal(new DateOnly(2026, 1, 1), retained.StartsOn);
        Assert.Null(retained.EndsOn);
        var original = await fresh.Set<RecurringInvoiceSchedule>().IgnoreQueryFilters().AsNoTracking().SingleAsync(row => row.Id == ScheduleId);
        Assert.Empty(original.GeneratedInvoices);
        Assert.Null(original.LastGeneratedAtUtc);
    }

    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Refused_stored_schedules_do_not_block_a_healthy_bulk_run_or_change_their_state()
    {
        var service = BuildService(new NodeRecurringInvoiceWriteEnlister());
        await service.PauseScheduleAsync(ScheduleId);
        var bad = new[]
        {
            (Id: new RecurringInvoiceScheduleId("00000000-0000-0000-0000-0000000000e1"),
                Rule: "FREQ=DAILY;INTERVAL=0", Lookahead: 2, Detail: "RecurrenceRule has an invalid INTERVAL value."),
            (Id: new RecurringInvoiceScheduleId("00000000-0000-0000-0000-0000000000e2"),
                Rule: "FREQ=DAILY;EXDATE=20260106", Lookahead: 2, Detail: "RecurrenceRule contains an unsupported component."),
            (Id: new RecurringInvoiceScheduleId("00000000-0000-0000-0000-0000000000e3"),
                Rule: "FREQ=DAILY", Lookahead: 1000, Detail: "RecurrenceRule exceeds the supported expansion limit for the requested range."),
        };
        var healthyId = new RecurringInvoiceScheduleId("00000000-0000-0000-0000-0000000000e4");
        await using (var setup = await _factory.CreateDbContextAsync())
        {
            foreach (var item in bad)
                setup.Set<RecurringInvoiceSchedule>().Add(StoredRefusalSchedule(item.Id, item.Rule, item.Lookahead));
            setup.Set<RecurringInvoiceSchedule>().Add(RecurringInvoiceSchedule.Create(
                LocalTenantId, new ChartOfAccountsId("CH-1"), new PartyId("customer-1"), new GLAccountId("1100"),
                "FREQ=MONTHLY;BYMONTHDAY=1", new DateOnly(2026, 1, 1), "UTC",
                new[] { new RecurringInvoiceLineTemplate("Healthy monthly", 1m, 250m, new GLAccountId("4000")) },
                endsOn: new DateOnly(2026, 1, 1), lookaheadHorizonDays: 0, generateLeadDays: 0, id: healthyId));
            await setup.SaveChangesAsync();
        }
        Assert.Equal(0, await CountJournalEntriesAsync());
        await using (var baseline = await _factory.CreateDbContextAsync())
            Assert.Equal(0, await baseline.Set<Invoice>().IgnoreQueryFilters().CountAsync());

        var results = await service.GenerateAllDueAsync(LocalTenantId, new DateOnly(2026, 1, 1), Authority);

        Assert.Equal(4, results.Count);
        foreach (var item in bad)
        {
            var refused = Assert.Single(results, result => result.ScheduleId == item.Id);
            Assert.Equal(ScheduleGenerationOutcome.Failed, refused.Outcome);
            Assert.Empty(refused.Invoices);
            Assert.Equal(item.Detail, refused.Error);
            await AssertStoredRefusalUnchangedAsync(item.Id, item.Rule, item.Lookahead);
        }
        var healthy = Assert.Single(results, result => result.ScheduleId == healthyId);
        Assert.Equal(ScheduleGenerationOutcome.Generated, healthy.Outcome);
        var generated = Assert.Single(healthy.Invoices);
        Assert.Equal(new DateOnly(2026, 1, 1), generated.OccurrenceDate);
        Assert.Equal(ScheduleGenerationOutcome.Generated, generated.Outcome);
        Assert.Equal(1, await CountJournalEntriesAsync());
        await using (var persisted = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(1, await persisted.Set<Invoice>().IgnoreQueryFilters().CountAsync());
            Assert.Equal(5, await persisted.Set<RecurringInvoiceSchedule>().IgnoreQueryFilters().CountAsync());
            var healthyState = await persisted.Set<RecurringInvoiceSchedule>().IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(row => row.Id == healthyId);
            Assert.Equal(new DateOnly(2026, 1, 1), Assert.Single(healthyState.GeneratedInvoices).Key);
            Assert.Equal(new DateTimeOffset(2026, 3, 1, 7, 8, 9, TimeSpan.Zero), healthyState.LastGeneratedAtUtc);
            var original = await persisted.Set<RecurringInvoiceSchedule>().IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(row => row.Id == ScheduleId);
            Assert.Equal(RecurringScheduleStatus.Paused, original.Status);
            Assert.Empty(original.GeneratedInvoices);
            Assert.Null(original.LastGeneratedAtUtc);
        }

        var reopened = BuildService(new NodeRecurringInvoiceWriteEnlister());
        var repeated = await reopened.GenerateAllDueAsync(LocalTenantId, new DateOnly(2026, 1, 1), Authority);
        Assert.Equal(4, repeated.Count);
        foreach (var item in bad)
        {
            var refused = Assert.Single(repeated, result => result.ScheduleId == item.Id);
            Assert.Equal(ScheduleGenerationOutcome.Failed, refused.Outcome);
            Assert.Empty(refused.Invoices);
            Assert.Equal(item.Detail, refused.Error);
            await AssertStoredRefusalUnchangedAsync(item.Id, item.Rule, item.Lookahead);
        }
        var repeatHealthy = Assert.Single(repeated, result => result.ScheduleId == healthyId);
        Assert.Equal(ScheduleGenerationOutcome.Generated, repeatHealthy.Outcome);
        Assert.Equal(ScheduleGenerationOutcome.AlreadyGenerated, Assert.Single(repeatHealthy.Invoices).Outcome);
        Assert.Equal(1, await CountJournalEntriesAsync());
        await using var finalRead = await _factory.CreateDbContextAsync();
        Assert.Equal(1, await finalRead.Set<Invoice>().IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task Recognized_exception_type_from_the_store_is_not_mapped_as_a_recurrence_refusal()
    {
        var service = ServiceWithScheduleFactory(new FormatFailureContextFactory());

        var create = await Assert.ThrowsAsync<FormatException>(() => CreateValidCandidateAsync(service));
        Assert.Equal("context-factory-format-fault", create.Message);
        var generate = await Assert.ThrowsAsync<FormatException>(
            () => service.GenerateDueInvoicesAsync(ScheduleId, OccurrenceDate, Authority));
        Assert.Equal("context-factory-format-fault", generate.Message);

        await using var context = await _factory.CreateDbContextAsync();
        Assert.Equal(1, await context.Set<RecurringInvoiceSchedule>().IgnoreQueryFilters().CountAsync());
        Assert.Equal(0, await context.Set<Invoice>().IgnoreQueryFilters().CountAsync());
        Assert.Equal(0, await CountJournalEntriesAsync());
        var original = await context.Set<RecurringInvoiceSchedule>().IgnoreQueryFilters().AsNoTracking().SingleAsync();
        Assert.Empty(original.GeneratedInvoices);
        Assert.Null(original.LastGeneratedAtUtc);
        Assert.Equal(RecurringScheduleStatus.Active, original.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_during_the_expansion_boundary_stays_cancellation_even_when_the_call_refuses(bool refuse)
    {
        using var createCancellation = new CancellationTokenSource();
        var createExpansion = new CancellingExpansion(createCancellation, refuse);
        var createService = ServiceWithScheduleFactory(_factory, createExpansion);
        var create = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateValidCandidateAsync(createService, createCancellation.Token));
        Assert.Equal(createCancellation.Token, create.CancellationToken);
        Assert.Equal(1, createExpansion.Calls);

        using var generateCancellation = new CancellationTokenSource();
        var generateExpansion = new CancellingExpansion(generateCancellation, refuse);
        var generateService = ServiceWithScheduleFactory(_factory, generateExpansion);
        var generate = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => generateService.GenerateDueInvoicesAsync(ScheduleId, OccurrenceDate, Authority, generateCancellation.Token));
        Assert.Equal(generateCancellation.Token, generate.CancellationToken);
        Assert.Equal(1, generateExpansion.Calls);

        await using var context = await _factory.CreateDbContextAsync();
        Assert.Equal(1, await context.Set<RecurringInvoiceSchedule>().IgnoreQueryFilters().CountAsync());
        Assert.Equal(0, await context.Set<Invoice>().IgnoreQueryFilters().CountAsync());
        Assert.Equal(0, await CountJournalEntriesAsync());
        var original = await context.Set<RecurringInvoiceSchedule>().IgnoreQueryFilters().AsNoTracking().SingleAsync();
        Assert.Empty(original.GeneratedInvoices);
        Assert.Null(original.LastGeneratedAtUtc);
    }

    [Fact]
    public async Task A_different_boundary_tenant_is_not_a_recurrence_failed_result()
    {
        var service = BuildService(new NodeRecurringInvoiceWriteEnlister());
        var authority = TestAuthorization.Write(new TenantId("other-tenant"), Actor.Value,
            new DateTimeOffset(2026, 3, 1, 7, 8, 9, TimeSpan.Zero));

        var refused = await Assert.ThrowsAsync<ArgumentException>(
            () => service.GenerateDueInvoicesAsync(ScheduleId, OccurrenceDate, authority));

        Assert.Equal("authority", refused.ParamName);
        Assert.Equal(0, await CountJournalEntriesAsync());
        await using var context = await _factory.CreateDbContextAsync();
        Assert.Equal(0, await context.Set<Invoice>().IgnoreQueryFilters().CountAsync());
        var original = await context.Set<RecurringInvoiceSchedule>().IgnoreQueryFilters().AsNoTracking().SingleAsync();
        Assert.Empty(original.GeneratedInvoices);
        Assert.Null(original.LastGeneratedAtUtc);
    }

    [Fact]
    public async Task Unrecognized_expansion_failure_propagates_before_create_or_generation_writes()
    {
        var failure = new InvalidOperationException("unrecognized-expansion-fault");
        var expansion = new UnrecognizedExpansion(failure);
        var service = ServiceWithScheduleFactory(_factory, expansion);

        var create = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateValidCandidateAsync(service));
        Assert.Same(failure, create);
        Assert.Equal(1, expansion.Calls);
        await AssertUnknownExpansionLeftSeedUnchangedAsync();

        var generate = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GenerateDueInvoicesAsync(ScheduleId, OccurrenceDate, Authority));
        Assert.Same(failure, generate);
        Assert.Equal(2, expansion.Calls);
        await AssertUnknownExpansionLeftSeedUnchangedAsync();
    }

    private async Task AssertUnknownExpansionLeftSeedUnchangedAsync()
    {
        await using var context = await _factory.CreateDbContextAsync();
        Assert.Equal(1, await context.Set<RecurringInvoiceSchedule>().IgnoreQueryFilters().CountAsync());
        Assert.Equal(0, await context.Set<Invoice>().IgnoreQueryFilters().CountAsync());
        Assert.Equal(0, await CountJournalEntriesAsync());
        var stored = await context.Set<RecurringInvoiceSchedule>().IgnoreQueryFilters().AsNoTracking().SingleAsync();
        Assert.Equal("00000000-0000-0000-0000-0000000000a1", stored.Id.Value);
        Assert.Equal(LocalTenantId, stored.TenantId);
        Assert.Equal("CH-1", stored.ChartId.Value);
        Assert.Equal("customer-1", stored.CustomerId.Value);
        Assert.Equal("1100", stored.ArAccountId.Value);
        Assert.Equal("FREQ=DAILY;INTERVAL=1", stored.RecurrenceRule);
        Assert.Equal(new DateOnly(2026, 3, 1), stored.StartsOn);
        Assert.Equal((DateOnly?)new DateOnly(2026, 3, 1), stored.EndsOn);
        Assert.Equal("UTC", stored.Timezone);
        Assert.Equal(0, stored.LookaheadHorizonDays);
        Assert.Equal(0, stored.GenerateLeadDays);
        Assert.Equal(RecurringScheduleStatus.Active, stored.Status);
        Assert.Empty(stored.GeneratedInvoices);
        Assert.Null(stored.LastGeneratedAtUtc);
        var line = Assert.Single(stored.LineTemplates);
        Assert.Equal("Monthly service", line.Description);
        Assert.Equal(1m, line.Quantity);
        Assert.Equal(250m, line.UnitPrice);
        Assert.Equal("4000", line.IncomeAccountId.Value);
    }

    private sealed class UnrecognizedExpansion(InvalidOperationException failure) : IRruleExpansionService
    {
        public int Calls { get; private set; }

        public IReadOnlyList<DateOnly> ExpandOccurrences(string rrule, DateOnly start, DateOnly? end,
            int lookaheadDays, int leadDays, DateOnly today, string timezone)
        {
            Calls++;
            throw failure;
        }
    }

    [Theory]
    [InlineData(false, "RRULE FREQ 'HOURLY' is not supported.", "RecurrenceRule has an unsupported FREQ value.")]
    [InlineData(true, "RRULE component 'COUNT' value 'x' is invalid.", "RecurrenceRule contains a malformed value.")]
    [InlineData(true, "RRULE component 'COUNT' value 'x' is not a positive integer. trailing", "RecurrenceRule contains a malformed value.")]
    [InlineData(true, "unanchored INTERVAL bad value", "RecurrenceRule contains a malformed value.")]
    [InlineData(true, "rrule component 'COUNT' value 'x' is not a positive integer.", "RecurrenceRule contains a malformed value.")]
    [InlineData(true, "RRULE component 'COUNT' value 'x' value 'BYDAY' is not a positive integer.", "RecurrenceRule contains a malformed value.")]
    [InlineData(true, "RRULE component 'COUNT' value '' is not a positive integer.", "RecurrenceRule contains a malformed value.")]
    [InlineData(true, "RRULE FREQ 'HOURLY' is not supported.", "RecurrenceRule contains a malformed value.")]
    [InlineData(false, "RRULE component 'COUNT' value 'x' is not a positive integer.", "RecurrenceRule contains an unsupported component.")]
    // Leading text before the producer's prefix. The first row is the one an unanchored (Contains) prefix test
    // would map: its prefix-length span is the single space before the shared closing/opening quote. In the
    // second row that span includes the prefix's own quote, so it stays generic whether or not the prefix is anchored.
    [InlineData(true, "x RRULE component 'COUNT' value ' is not a positive integer.", "RecurrenceRule contains a malformed value.")]
    [InlineData(true, "x RRULE component 'COUNT' value 'x' is not a positive integer.", "RecurrenceRule contains a malformed value.")]
    public async Task Recurrence_message_mapping_requires_the_exact_type_and_complete_pinned_template(
        bool malformed, string message, string expectedDetail)
    {
        Exception failure = malformed ? new FormatException(message) : new NotSupportedException(message);
        await AssertMappedRecurrenceFailureAsync(failure, expectedDetail);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exception_subclass_message_getters_are_never_read_for_create_or_generation(bool malformed)
    {
        if (malformed)
        {
            var failure = new HostileRecurrenceFormatException();
            await AssertMappedRecurrenceFailureAsync(failure, "RecurrenceRule contains a malformed value.",
                () => failure.MessageReads);
        }
        else
        {
            var failure = new HostileRecurrenceNotSupportedException();
            await AssertMappedRecurrenceFailureAsync(failure, "RecurrenceRule contains an unsupported component.",
                () => failure.MessageReads);
        }
    }

    private async Task AssertMappedRecurrenceFailureAsync(
        Exception failure, string expectedDetail, Func<int>? messageReads = null)
    {
        var expansion = new DiagnosticRecurrenceExpansion(failure);
        var service = ServiceWithScheduleFactory(_factory, expansion);
        var create = await Assert.ThrowsAsync<RecurringInvoiceRecurrenceValidationException>(
            () => CreateValidCandidateAsync(service));
        Assert.Equal(expectedDetail, create.SafeDetail);
        Assert.Equal(expectedDetail, create.Message);
        Assert.InRange(create.SafeDetail.Length, 1, 160);
        Assert.All(create.SafeDetail, character => Assert.False(char.IsControl(character)));
        Assert.Equal(1, expansion.Calls);
        if (messageReads is not null) Assert.Equal(0, messageReads());
        await AssertUnknownExpansionLeftSeedUnchangedAsync();

        var generate = await service.GenerateDueInvoicesAsync(ScheduleId, OccurrenceDate, Authority);
        Assert.Equal(ScheduleGenerationOutcome.Failed, generate.Outcome);
        Assert.Empty(generate.Invoices);
        Assert.Equal(expectedDetail, generate.Error);
        Assert.InRange(generate.Error!.Length, 1, 160);
        Assert.All(generate.Error!, character => Assert.False(char.IsControl(character)));
        Assert.Equal(2, expansion.Calls);
        if (messageReads is not null) Assert.Equal(0, messageReads());
        await AssertUnknownExpansionLeftSeedUnchangedAsync();
    }

    private sealed class DiagnosticRecurrenceExpansion(Exception failure) : IRruleExpansionService
    {
        public int Calls { get; private set; }

        public IReadOnlyList<DateOnly> ExpandOccurrences(string rrule, DateOnly start, DateOnly? end,
            int lookaheadDays, int leadDays, DateOnly today, string timezone)
        {
            Calls++;
            throw failure;
        }
    }

    private sealed class HostileRecurrenceFormatException : FormatException
    {
        public int MessageReads { get; private set; }

        public override string Message
        {
            get
            {
                MessageReads++;
                throw new InvalidOperationException("The custom diagnostic getter must not run.");
            }
        }
    }

    private sealed class HostileRecurrenceNotSupportedException : NotSupportedException
    {
        public int MessageReads { get; private set; }

        public override string Message
        {
            get
            {
                MessageReads++;
                throw new InvalidOperationException("The custom diagnostic getter must not run.");
            }
        }
    }

    private static RecurringInvoiceSchedule StoredRefusalSchedule(RecurringInvoiceScheduleId id, string rule, int lookahead)
        => RecurringInvoiceSchedule.Create(
            LocalTenantId, new ChartOfAccountsId("CH-1"), new PartyId("customer-1"), new GLAccountId("1100"),
            rule, new DateOnly(2026, 1, 1), "UTC",
            new[] { new RecurringInvoiceLineTemplate("Stored refusal", 1m, 250m, new GLAccountId("4000")) },
            endsOn: null, lookaheadHorizonDays: lookahead, generateLeadDays: 0, id: id);

    private async Task AssertStoredRefusalUnchangedAsync(RecurringInvoiceScheduleId id, string rule, int lookahead)
    {
        await using var context = await _factory.CreateDbContextAsync();
        var stored = await context.Set<RecurringInvoiceSchedule>().IgnoreQueryFilters().AsNoTracking().SingleAsync(row => row.Id == id);
        Assert.Equal(LocalTenantId, stored.TenantId);
        Assert.Equal("CH-1", stored.ChartId.Value);
        Assert.Equal("customer-1", stored.CustomerId.Value);
        Assert.Equal("1100", stored.ArAccountId.Value);
        Assert.Equal(rule, stored.RecurrenceRule);
        Assert.Equal(new DateOnly(2026, 1, 1), stored.StartsOn);
        Assert.Null(stored.EndsOn);
        Assert.Equal("UTC", stored.Timezone);
        Assert.Equal(lookahead, stored.LookaheadHorizonDays);
        Assert.Equal(0, stored.GenerateLeadDays);
        Assert.Equal(RecurringScheduleStatus.Active, stored.Status);
        Assert.Empty(stored.GeneratedInvoices);
        Assert.Null(stored.LastGeneratedAtUtc);
        var line = Assert.Single(stored.LineTemplates);
        Assert.Equal("Stored refusal", line.Description);
        Assert.Equal(1m, line.Quantity);
        Assert.Equal(250m, line.UnitPrice);
        Assert.Equal("4000", line.IncomeAccountId.Value);
    }

    private static Task<RecurringInvoiceSchedule> CreateValidCandidateAsync(
        NodeEfRecurringInvoiceService service, CancellationToken cancellationToken = default)
        => service.CreateScheduleAsync(
            LocalTenantId, new ChartOfAccountsId("CH-1"), new PartyId("customer-1"), new GLAccountId("1100"),
            "FREQ=MONTHLY;BYMONTHDAY=1", new DateOnly(2026, 1, 1), "UTC",
            new[] { new RecurringInvoiceLineTemplate("Valid candidate", 1m, 250m, new GLAccountId("4000")) },
            endsOn: new DateOnly(2026, 1, 1), lookaheadHorizonDays: 0, generateLeadDays: 0,
            cancellationToken: cancellationToken);

    private NodeEfRecurringInvoiceService ServiceWithScheduleFactory(
        IDbContextFactory<LocalNodeDbContext> schedules, IRruleExpansionService? expansion = null)
    {
        var invoices = new NodeEfInvoiceRepository(_factory);
        var journalStore = new NodeEfJournalStore(_factory,
            NodeJournalWriteAdapters.Create(recurringInvoice: new NodeRecurringInvoiceWriteEnlister()));
        var posting = new InvoicePostingService(
            tenantContext: new ActiveTeamTenantContext(NodeTestActiveTeam.Accessor),
            invoices: invoices,
            numbering: new NodeEfInvoiceNumberingService(_factory, new ReplicaId("AA")),
            tax: new NoOpTaxCalculator(),
            journals: new JournalPostingService(
                accounts: new NodeEfAccountResolver(_factory), periods: new NodeEfPeriodResolver(_factory),
                store: journalStore, gate: TestAuthorization.AllowGate()),
            events: null, journalStore: journalStore, timeProvider: TimeProvider.System);
        return new NodeEfRecurringInvoiceService(schedules, expansion ?? new InMemoryRruleExpansionService(), posting, invoices);
    }

    private sealed class FormatFailureContextFactory : IDbContextFactory<LocalNodeDbContext>
    {
        public LocalNodeDbContext CreateDbContext() => throw new FormatException("context-factory-format-fault");

        public Task<LocalNodeDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromException<LocalNodeDbContext>(new FormatException("context-factory-format-fault"));
    }

    private sealed class CancellingExpansion(CancellationTokenSource cancellation, bool refuse) : IRruleExpansionService
    {
        private readonly IRruleExpansionService _inner = new InMemoryRruleExpansionService();
        public int Calls { get; private set; }

        public IReadOnlyList<DateOnly> ExpandOccurrences(string rrule, DateOnly start, DateOnly? end,
            int lookaheadDays, int leadDays, DateOnly today, string timezone)
        {
            Calls++;
            var dates = _inner.ExpandOccurrences(rrule, start, end, lookaheadDays, leadDays, today, timezone);
            cancellation.Cancel();
            if (refuse) throw new FormatException("cancelled-at-expansion-boundary");
            return dates;
        }
    }

    //  THE REGRESSION GATE — crash between effect and idempotency, then resume.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "bug-1337: crash injected at the effect→idempotency window then RESUME posts EXACTLY ONE JE (no double-post)")]
    public async Task Crash_BetweenEffectAndIdempotency_ResumePostsExactlyOneJournalEntry()
    {
        // ── RUN 1 (crashes) ──
        // A crashing enlister throws WHERE the prior two-transaction shape had its fatal window: in the
        // post pipeline, after the JE row is staged but BEFORE the single atomic SaveChangesAsync. Because
        // the JE + idempotency now share one transaction, the throw rolls BOTH back — no orphan JE, no
        // idempotency record. (Under the OLD shape, the JE would already be committed here → orphan.)
        var crashingService = BuildService(new CrashingEnlister(new NodeRecurringInvoiceWriteEnlister()));

        var crash = await Assert.ThrowsAnyAsync<Exception>(() =>
            crashingService.GenerateDueInvoicesAsync(ScheduleId, OccurrenceDate, Authority));
        Assert.Contains("SIMULATED CRASH", FlattenMessages(crash));

        // The crash rolled the whole unit-of-work back: NO JE, NO idempotency record persisted.
        Assert.Equal(0, await CountJournalEntriesAsync());
        Assert.False(await OccurrenceIsRecordedAsync(),
            "after an atomic-advance crash the idempotency record must NOT be persisted (it rolled back with the JE)");

        // ── RESUME (process restart — fresh service over the SAME db, real enlister, no crash) ──
        var resumedService = BuildService(new NodeRecurringInvoiceWriteEnlister());
        var result = await resumedService.GenerateDueInvoicesAsync(ScheduleId, OccurrenceDate, Authority);

        // The resume generated the occurrence exactly once.
        Assert.Equal(ScheduleGenerationOutcome.Generated, result.Outcome);
        Assert.Single(result.Invoices);
        Assert.Equal(ScheduleGenerationOutcome.Generated, result.Invoices[0].Outcome);

        // PROOF OF FIX: exactly ONE journal entry for the one occurrence — no double-post.
        Assert.Equal(1, await CountJournalEntriesAsync());
        Assert.True(await OccurrenceIsRecordedAsync(),
            "after a clean resume the occurrence must be recorded in the durable idempotency map");

        // A SECOND resume run is a pure idempotency hit — still exactly one JE.
        var secondResume = await resumedService.GenerateDueInvoicesAsync(ScheduleId, OccurrenceDate, Authority);
        Assert.All(secondResume.Invoices, e => Assert.Equal(ScheduleGenerationOutcome.AlreadyGenerated, e.Outcome));
        Assert.Equal(1, await CountJournalEntriesAsync());
    }

    [Fact(DisplayName = "bug-1337 backstop (fix a): a leaked JE with NO idempotency record (legacy orphan) is deduped by the stable source-reference on resume — still EXACTLY ONE JE")]
    public async Task LeakedJournalEntry_NoIdempotencyRecord_ResumeStableSourceRefDedupes()
    {
        // Simulate the EXACT legacy failure state directly: a JE was committed for the occurrence but the
        // idempotency record was NOT written (the orphan the two-transaction shape produced). With fix (a)
        // the leaked JE carries the DETERMINISTIC source reference invoice:{stableId}. So when the resumed
        // run derives the SAME stable id and drives IssueAsync, the JournalPostingService phase-1.5 posting
        // idempotency (FindBySourceReferenceAsync on the persisted SourceReference, ADR 0122 §D2) finds the
        // existing JE and returns it as an idempotent re-drive — NO second JE is posted. This is the
        // backstop the stable key buys INDEPENDENTLY of the atomic co-commit: even if a JE ever leaked
        // without its record, the deterministic source reference prevents a double-post on resume.
        var stableId = NodeEfRecurringInvoiceService.DeriveOccurrenceInvoiceId(ScheduleId, OccurrenceDate);
        var orphanStore = new NodeEfJournalStore(_factory, NodeJournalWriteAdapters.Create());
        await orphanStore.SaveAtomicForTestAsync(LocalTenantId, BalancedIssueEntry(stableId, 250m));

        Assert.Equal(1, await CountJournalEntriesAsync());
        Assert.False(await OccurrenceIsRecordedAsync()); // the orphan: JE present, record absent

        // RESUME: the service derives the SAME stable id → IssueAsync posts a JE with the SAME source
        // reference → phase-1.5 returns the already-posted JE without re-posting.
        var resumedService = BuildService(new NodeRecurringInvoiceWriteEnlister());
        var result = await resumedService.GenerateDueInvoicesAsync(ScheduleId, OccurrenceDate, Authority);

        Assert.Equal(ScheduleGenerationOutcome.Generated, result.Outcome);

        // PROOF: the stable source reference held the line — still EXACTLY ONE journal entry (no
        // double-post), even though the leaked JE had no idempotency record. On this idempotent re-drive
        // the JE commit (and therefore the enlister) does not run, so the schedule's durable record stays
        // absent — but the deterministic source reference deduplicates the JE on EVERY future resume, so
        // the occurrence can never double-post. The financial invariant (one occurrence == one JE) holds
        // regardless. A repeat run stays at one JE.
        Assert.Equal(1, await CountJournalEntriesAsync());
        var repeat = await resumedService.GenerateDueInvoicesAsync(ScheduleId, OccurrenceDate, Authority);
        Assert.Equal(ScheduleGenerationOutcome.Generated, repeat.Outcome);
        Assert.Equal(1, await CountJournalEntriesAsync());
    }

    [Fact(DisplayName = "bug-1337 happy path: a clean generation posts one JE AND its idempotency record atomically")]
    public async Task CleanGeneration_PostsJournalEntryAndIdempotencyRecordAtomically()
    {
        var service = BuildService(new NodeRecurringInvoiceWriteEnlister());
        var result = await service.GenerateDueInvoicesAsync(ScheduleId, OccurrenceDate, Authority);

        Assert.Equal(ScheduleGenerationOutcome.Generated, result.Outcome);
        Assert.Equal(1, await CountJournalEntriesAsync());
        Assert.True(await OccurrenceIsRecordedAsync());

        // The recorded invoice id is the DETERMINISTIC occurrence id (fix a), not a random GUID.
        var stableId = NodeEfRecurringInvoiceService.DeriveOccurrenceInvoiceId(ScheduleId, OccurrenceDate);
        await using var ctx = await _factory.CreateDbContextAsync();
        var schedule = await ctx.Set<RecurringInvoiceSchedule>()
            .IgnoreQueryFilters()
            .FirstAsync(s => s.Id == ScheduleId);
        Assert.True(schedule.GeneratedInvoices.TryGetValue(OccurrenceDate, out var recordedId));
        Assert.Equal(stableId, recordedId);
        Assert.Equal(Authority.At, schedule.LastGeneratedAtUtc);
    }

    // ── service composition (production-faithful) ─────────────────────────────

    private NodeEfRecurringInvoiceService BuildService(INodeRecurringInvoiceWriteEnlister recurringEnlister)
    {
        var invoices = new NodeEfInvoiceRepository(_factory);
        var numbering = new NodeEfInvoiceNumberingService(_factory, new ReplicaId("AA"));
        // The recurring enlister rides the journal store's single save — exactly the production wiring.
        var journalStore = new NodeEfJournalStore(
            _factory,
            NodeJournalWriteAdapters.Create(recurringInvoice: (Harborline.Api.Foundation.Coordination.IWriteEnlistment)recurringEnlister));
        var journalPosting = new JournalPostingService(
            accounts: new NodeEfAccountResolver(_factory),
            periods:  new NodeEfPeriodResolver(_factory),
            store:    journalStore,
            gate:     TestAuthorization.AllowGate());
        var invoicePosting = new InvoicePostingService(
            tenantContext: new ActiveTeamTenantContext(NodeTestActiveTeam.Accessor),
            invoices:      invoices,
            numbering:     numbering,
            tax:           new NoOpTaxCalculator(),
            journals:      journalPosting,
            events:        null,
            journalStore:  journalStore, timeProvider: TimeProvider.System);

        return new NodeEfRecurringInvoiceService(
            contextFactory: _factory,
            rrule:          new InMemoryRruleExpansionService(),
            posting:        invoicePosting,
            invoices:       invoices);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private async Task<int> CountJournalEntriesAsync()
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        return await ctx.Set<JournalEntry>().CountAsync(j => j.TenantId == LocalTenantId);
    }

    private async Task<bool> OccurrenceIsRecordedAsync()
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        var schedule = await ctx.Set<RecurringInvoiceSchedule>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == ScheduleId);
        return schedule is not null && schedule.GeneratedInvoices.ContainsKey(OccurrenceDate);
    }

    private static string FlattenMessages(Exception ex)
    {
        var msg = ex.Message;
        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
            msg += " | " + inner.Message;
        return msg;
    }

    private JournalEntry BalancedIssueEntry(InvoiceId invoiceId, decimal amount) =>
        new JournalEntry(
            id: JournalEntryId.NewId(),
            tenantId: LocalTenantId,
            entryDate: OccurrenceDate,
            memo: "leaked issue JE (no idempotency record)",
            lines: new List<JournalEntryLine>
            {
                new(new GLAccountId("1100"), amount, 0m),
                new(new GLAccountId("4000"), 0m, amount),
            },
            createdAtUtc: new Instant(System.TimeProvider.System.GetUtcNow()),
            sourceReference: $"invoice:{invoiceId.Value}")
        {
            ChartId = new ChartOfAccountsId("CH-1"),
            SourceKind = JournalEntrySource.Invoice,
            Status = JournalEntryStatus.Posted,
        };

    private async Task SeedAccountAsync(string code, string name, GLAccountType type, AccountSubtype subtype)
    {
        var account = GLAccount.Create(
            id:           new GLAccountId(code),
            chartId:      new ChartOfAccountsId("CH-1"),
            code:         code,
            name:         name,
            type:         type,
            subtype:      subtype,
            currency:     "USD",
            isPostable:   true,
            createdAtUtc: new Instant(System.TimeProvider.System.GetUtcNow()));

        await using var ctx = await _factory.CreateDbContextAsync();
        ctx.Set<GLAccount>().Add(account);
        await ctx.SaveChangesAsync();
    }

    private async Task SeedOpenPeriodAsync(DateOnly start, DateOnly end)
    {
        var period = FiscalPeriod.CreateOpen(
            id:           FiscalPeriodId.NewId(),
            chartId:      new ChartOfAccountsId("CH-1"),
            fiscalYearId: new FiscalYearId("FY-2026"),
            kind:         FiscalPeriodKind.Monthly,
            label:        $"{start:yyyy-MM}",
            startDate:    start,
            endDate:      end,
            createdAtUtc: new Instant(System.TimeProvider.System.GetUtcNow()));

        await using var ctx = await _factory.CreateDbContextAsync();
        ctx.Set<FiscalPeriod>().Add(period);
        await ctx.SaveChangesAsync();
    }

    private async Task SeedScheduleAsync()
    {
        // A daily RRULE that produces the single OccurrenceDate when expanded with asOf == OccurrenceDate
        // and a zero lookahead/lead window. The line template posts $250 to income 4000 / AR 1100.
        var schedule = RecurringInvoiceSchedule.Create(
            tenantId:        LocalTenantId,
            chartId:         new ChartOfAccountsId("CH-1"),
            customerId:      new PartyId("customer-1"),
            arAccountId:     new GLAccountId("1100"),
            recurrenceRule:  "FREQ=DAILY;INTERVAL=1",
            startsOn:        OccurrenceDate,
            timezone:        "UTC",
            lineTemplates:   new[]
            {
                new RecurringInvoiceLineTemplate(
                    Description: "Monthly service",
                    Quantity: 1m,
                    UnitPrice: 250m,
                    IncomeAccountId: new GLAccountId("4000")),
            },
            endsOn:               OccurrenceDate,
            lookaheadHorizonDays: 0,
            generateLeadDays:     0,
            id:                   ScheduleId);

        await using var ctx = await _factory.CreateDbContextAsync();
        ctx.Set<RecurringInvoiceSchedule>().Add(schedule);
        await ctx.SaveChangesAsync();
    }

    /// <summary>
    /// Wraps the real recurring enlister and throws AFTER it stages the idempotency record but BEFORE the
    /// journal store's single SaveChangesAsync — the crash window at the heart of bug-1337. Under the
    /// atomic-advance fix the throw rolls BACK the staged JE + the staged record together.
    /// </summary>
    private sealed class CrashingEnlister : INodeRecurringInvoiceWriteEnlister, Harborline.Api.Foundation.Coordination.IWriteEnlistment
    {
        private readonly INodeRecurringInvoiceWriteEnlister _inner;
        public CrashingEnlister(INodeRecurringInvoiceWriteEnlister inner) => _inner = inner;

        public Harborline.Api.Foundation.Coordination.WriteInvariant Invariant => NodeWriteInvariants.RecurringInvoice;

        public async ValueTask<Harborline.Api.Foundation.Coordination.WriteEnlistmentOutcome> EnlistAsync(
            Harborline.Api.Foundation.Coordination.StagedWriteUnitOfWork unitOfWork,
            CancellationToken cancellationToken = default)
        {
            await ((Harborline.Api.Foundation.Coordination.IWriteEnlistment)_inner)
                .EnlistAsync(unitOfWork, cancellationToken);
            throw new InvalidOperationException("SIMULATED CRASH @ effect→idempotency window (bug-1337)");
        }

        public async Task EnlistRecurringIdempotencyAsync(
            LocalNodeDbContext ctx, JournalEntry entry, CancellationToken ct = default)
        {
            await _inner.EnlistRecurringIdempotencyAsync(ctx, entry, ct);
            // The JE row + the idempotency record are BOTH staged on ctx now; the single SaveChangesAsync
            // has NOT run. A crash here must leave NEITHER persisted (atomic rollback).
            throw new InvalidOperationException("SIMULATED CRASH @ effect→idempotency window (bug-1337)");
        }
    }
}
