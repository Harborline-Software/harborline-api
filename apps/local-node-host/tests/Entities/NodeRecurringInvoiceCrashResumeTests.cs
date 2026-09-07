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

    // ─────────────────────────────────────────────────────────────────────────
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
