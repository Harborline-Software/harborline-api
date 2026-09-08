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
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Audit;
using Harborline.Api.LocalNodeHost.Data.Financial;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// ADR 0135 F3 — crash-resume regression for the invoice <c>Draft → Issued</c> transition in
/// <see cref="InvoicePostingService.IssueAsync"/> over the recoverable node stack.
///
/// <para>
/// <b>The window.</b> <c>IssueAsync</c> posts the issue JE atomically (JE + audit + recurring idempotency,
/// one SQLite transaction via <see cref="NodeEfJournalStore"/>) and historically flipped the invoice
/// <c>Draft → Issued</c> in a SEPARATE transaction afterwards. A crash between the JE commit and that
/// status upsert left a POSTED JE + a STRANDED <c>Draft</c> invoice — no double-post, but a ledger↔AR
/// consistency edge (the de-reviews on earlier repository ticket #1346/#1348 flagged it as F3).
/// </para>
///
/// <para>
/// <b>The fix.</b> <c>IssueAsync</c> opens an ambient <see cref="IssuedInvoiceWriteScope"/> carrying the
/// fully-built issued invoice + the stable JE source reference; the node's
/// <see cref="NodeIssuedInvoiceWriteEnlister"/> (riding <see cref="NodeEfJournalStore"/>'s single
/// <c>SaveChangesAsync</c>) stages the <c>Draft → Issued</c> update onto the SAME context, so { JE + audit
/// + recurring-idempotency + invoice status } commit (or roll back) in ONE SQLite transaction. The
/// posting service then SKIPS its separate upsert. A crash before that single save rolls the JE AND the
/// status update back together (invoice stays Draft, no JE); a resume re-issues cleanly.
/// </para>
///
/// <para>
/// These tests build the PRODUCTION node posting stack (the real <see cref="InvoicePostingService"/> over
/// the real <see cref="JournalPostingService"/> + node resolvers + <see cref="NodeEfJournalStore"/> + the
/// new <see cref="NodeIssuedInvoiceWriteEnlister"/>) over an on-disk SQLite <see cref="LocalNodeDbContext"/>,
/// then exercise both the forward atomic guarantee (a crash leaves a consistent Draft) and the legacy
/// self-heal (a pre-existing posted-JE + stranded-Draft is re-issued on resume — no double-post).
/// </para>
/// </summary>
public sealed class NodeIssuedInvoiceCrashResumeTests : IAsyncLifetime
{
    private string _dir = null!;
    private IDbContextFactory<LocalNodeDbContext> _factory = null!;

    private static readonly TenantId LocalTenantId =
        ActiveTeamTenantContext.ProjectTenantId(NodeTestActiveTeam.TestTeamId);

    private static readonly PartyId Actor = new("actor-1");
    private static readonly Harborline.Api.Foundation.Authorization.AuthorizationWriteContext Authority =
        TestAuthorization.Write(LocalTenantId, Actor.Value);
    private static readonly DateOnly IssueDate = new(2026, 3, 1);
    private static readonly ChartOfAccountsId ChartId = new("CH-1");
    private static readonly GLAccountId ArAccountId = new("1100");
    private static readonly GLAccountId IncomeAccountId = new("4000");
    private static readonly InvoiceId DraftInvoiceId = new("11111111-1111-1111-1111-111111111111");

    public async Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "harborline-issued-crash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var connectionString = $"Data Source={Path.Combine(_dir, "issued-test.db")};Pooling=False";

        var services = new ServiceCollection();
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
    }

    public async Task DisposeAsync()
    {
        await Task.CompletedTask;
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  THE F3 REGRESSION GATE — crash between the JE commit and the Draft→Issued
    //  update, then resume → a CONSISTENT state (Issued + JE, no stranded Draft).
    // ─────────────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "ADR 0135 F3: a crash at the JE→(Draft→Issued) window rolls BOTH back (invoice stays Draft, NO JE), then RESUME issues exactly once (Issued + 1 JE — no stranded Draft, no double-post)")]
    public async Task Crash_BetweenJournalEntryAndStatusUpdate_ResumeReachesConsistentState()
    {
        await SeedDraftInvoiceAsync();

        // ── RUN 1 (crashes) ──
        // A crashing issued-invoice enlister throws AFTER staging the Draft→Issued update but BEFORE the
        // journal store's single SaveChangesAsync — exactly the F3 window. Because the JE + the status
        // update now share one transaction, the throw rolls BOTH back: the invoice stays Draft, no JE
        // lands. (Under the OLD non-atomic shape the JE would already be committed here → posted JE + a
        // stranded Draft.)
        var crashingPosting = BuildPostingService(new CrashingIssuedEnlister(new NodeIssuedInvoiceWriteEnlister()));

        var crash = await Assert.ThrowsAnyAsync<Exception>(() =>
            crashingPosting.IssueAsync(DraftInvoiceId, Authority));
        Assert.Contains("SIMULATED CRASH", FlattenMessages(crash));

        // The crash rolled the whole unit-of-work back: NO JE, and the invoice is STILL Draft (not stranded
        // as Issued-without-JE nor posted-JE-without-status).
        Assert.Equal(0, await CountJournalEntriesAsync());
        Assert.Equal(InvoiceStatus.Draft, await GetInvoiceStatusAsync());
        Assert.Null(await GetInvoiceJournalEntryIdAsync());

        // ── RESUME (process restart — fresh posting service over the SAME db, real enlister, no crash) ──
        var resumedPosting = BuildPostingService(new NodeIssuedInvoiceWriteEnlister());
        var result = await resumedPosting.IssueAsync(DraftInvoiceId, Authority);

        Assert.Equal(IssueError.None, result.Error);
        Assert.NotNull(result.Invoice);
        Assert.Equal(InvoiceStatus.Issued, result.Invoice!.Status);

        // PROOF OF FIX — a CONSISTENT state: exactly ONE JE AND the invoice is Issued with its JE id. No
        // stranded Draft, no orphan JE, no double-post.
        Assert.Equal(1, await CountJournalEntriesAsync());
        Assert.Equal(InvoiceStatus.Issued, await GetInvoiceStatusAsync());
        Assert.NotNull(await GetInvoiceJournalEntryIdAsync());

        // A second IssueAsync is a pure idempotency hit (already Issued) — still exactly one JE, still Issued.
        var secondIssue = await resumedPosting.IssueAsync(DraftInvoiceId, Authority);
        Assert.Equal(IssueError.None, secondIssue.Error);
        Assert.Equal(1, await CountJournalEntriesAsync());
        Assert.Equal(InvoiceStatus.Issued, await GetInvoiceStatusAsync());
    }

    [Fact(DisplayName = "ADR 0135 F3 legacy self-heal: a pre-existing posted JE + stranded Draft (the old non-atomic failure) is re-issued on resume — the stable source-reference dedups the JE, the Draft flips to Issued — still EXACTLY ONE JE")]
    public async Task LegacyStrandedDraft_PostedJeButStillDraft_ResumeHealsToConsistentState()
    {
        await SeedDraftInvoiceAsync();

        // Simulate the EXACT legacy failure state directly: the issue JE for this invoice was committed
        // (carrying the deterministic source reference invoice:{id}) but the Draft → Issued status update
        // was NOT applied (the crash the old two-step shape produced). The invoice is a stranded Draft.
        var orphanStore = new NodeEfJournalStore(_factory, NodeJournalWriteAdapters.Create());
        await orphanStore.SaveAtomicForTestAsync(LocalTenantId, BalancedIssueEntry(DraftInvoiceId, 250m));

        Assert.Equal(1, await CountJournalEntriesAsync());
        Assert.Equal(InvoiceStatus.Draft, await GetInvoiceStatusAsync()); // the stranded Draft

        // RESUME: IssueAsync finds the already-posted JE by the stable source reference (invoice:{id})
        // before attempting a new post. It repairs the legacy stranded Draft without creating another
        // ledger write; the declared enlistment path remains the only path used by new journal saves.
        var resumedPosting = BuildPostingService(new NodeIssuedInvoiceWriteEnlister());
        var result = await resumedPosting.IssueAsync(DraftInvoiceId, Authority);

        Assert.Equal(IssueError.None, result.Error);

        // PROOF: a consistent state — still EXACTLY ONE JE (the stable source reference dedup'd the JE) AND
        // the invoice is now Issued with that JE id. The legacy stranded Draft self-heals on the next issue.
        Assert.Equal(1, await CountJournalEntriesAsync());
        Assert.Equal(InvoiceStatus.Issued, await GetInvoiceStatusAsync());
        Assert.NotNull(await GetInvoiceJournalEntryIdAsync());

        var repeat = await resumedPosting.IssueAsync(DraftInvoiceId, Authority);
        Assert.Equal(IssueError.None, repeat.Error);
        Assert.Equal(1, await CountJournalEntriesAsync());
    }

    [Fact(DisplayName = "ADR 0135 F3 happy path: a clean issue posts ONE JE AND flips Draft → Issued ATOMICALLY (one transaction, no separate status upsert)")]
    public async Task CleanIssue_PostsJournalEntryAndFlipsStatusAtomically()
    {
        await SeedDraftInvoiceAsync();

        var posting = BuildPostingService(new NodeIssuedInvoiceWriteEnlister());
        var result = await posting.IssueAsync(DraftInvoiceId, Authority);

        Assert.Equal(IssueError.None, result.Error);
        Assert.Equal(InvoiceStatus.Issued, result.Invoice!.Status);
        Assert.Equal(1, await CountJournalEntriesAsync());
        Assert.Equal(InvoiceStatus.Issued, await GetInvoiceStatusAsync());

        // The persisted invoice points at the posted JE (the atomic co-commit recorded the JE id).
        var jeId = await GetInvoiceJournalEntryIdAsync();
        Assert.NotNull(jeId);
        var je = await GetTheSingleJournalEntryAsync();
        Assert.Equal($"invoice:{DraftInvoiceId.Value}", je.SourceReference);
    }

    // ── service composition (production-faithful) ─────────────────────────────

    private InvoicePostingService BuildPostingService(INodeIssuedInvoiceWriteEnlister issuedEnlister)
    {
        var invoices = new NodeEfInvoiceRepository(_factory);
        var numbering = new NodeEfInvoiceNumberingService(_factory, new ReplicaId("AA"));
        // The issued-invoice enlister rides the journal store's single save — exactly the production wiring.
        var journalStore = new NodeEfJournalStore(
            _factory,
            NodeJournalWriteAdapters.Create(issuedInvoice: (Harborline.Api.Foundation.Coordination.IWriteEnlistment)issuedEnlister));
        var journalPosting = new JournalPostingService(
            accounts: new NodeEfAccountResolver(_factory),
            periods:  new NodeEfPeriodResolver(_factory),
            store:    journalStore,
            gate:     TestAuthorization.AllowGate());
        return new InvoicePostingService(
            tenantContext: new ActiveTeamTenantContext(NodeTestActiveTeam.Accessor),
            invoices:      invoices,
            numbering:     numbering,
            tax:           new NoOpTaxCalculator(),
            journals:      journalPosting,
            events:        null,
            journalStore:  journalStore, timeProvider: TimeProvider.System);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private async Task<int> CountJournalEntriesAsync()
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        return await ctx.Set<JournalEntry>().CountAsync(j => j.TenantId == LocalTenantId);
    }

    private async Task<InvoiceStatus> GetInvoiceStatusAsync()
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        var invoice = await ctx.Set<Invoice>()
            .AsNoTracking()
            .FirstAsync(i => i.Id == DraftInvoiceId && i.TenantId == LocalTenantId);
        return invoice.Status;
    }

    private async Task<JournalEntryId?> GetInvoiceJournalEntryIdAsync()
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        var invoice = await ctx.Set<Invoice>()
            .AsNoTracking()
            .FirstAsync(i => i.Id == DraftInvoiceId && i.TenantId == LocalTenantId);
        return invoice.JournalEntryId;
    }

    private async Task<JournalEntry> GetTheSingleJournalEntryAsync()
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        return await ctx.Set<JournalEntry>().AsNoTracking().FirstAsync(j => j.TenantId == LocalTenantId);
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
            entryDate: IssueDate,
            memo: "leaked issue JE (stranded Draft — no status update)",
            lines: new List<JournalEntryLine>
            {
                new(ArAccountId, amount, 0m),
                new(IncomeAccountId, 0m, amount),
            },
            createdAtUtc: new Instant(System.TimeProvider.System.GetUtcNow()),
            sourceReference: $"invoice:{invoiceId.Value}")
        {
            ChartId = ChartId,
            SourceKind = JournalEntrySource.Invoice,
            Status = JournalEntryStatus.Posted,
        };

    private async Task SeedDraftInvoiceAsync()
    {
        // A Draft with a single $250 income line. The node create path mints a canonical number at create
        // time; seed one directly so the Draft already carries a well-formed number (matching production).
        var lines = new[]
        {
            InvoiceLine.Create(
                invoiceId:       DraftInvoiceId,
                lineNumber:      1,
                description:     "Service",
                quantity:        1m,
                unitPrice:       250m,
                incomeAccountId: IncomeAccountId),
        };
        var draft = Invoice.Create(
            tenantId:      LocalTenantId,
            chartId:       ChartId,
            invoiceNumber: "INV-2026-03-01-AA-0001",
            customerId:    new PartyId("customer-1"),
            issueDate:     IssueDate,
            dueDate:       IssueDate,
            lines:         lines,
            arAccountId:   ArAccountId,
            createdAtUtc:  new Instant(System.TimeProvider.System.GetUtcNow()),
            createdBy:     Actor,
            id:            DraftInvoiceId);

        var repo = new NodeEfInvoiceRepository(_factory);
        await repo.UpsertAsync(LocalTenantId, draft, new Instant(System.TimeProvider.System.GetUtcNow()).Value);
    }

    private async Task SeedAccountAsync(string code, string name, GLAccountType type, AccountSubtype subtype)
    {
        var account = GLAccount.Create(
            id:           new GLAccountId(code),
            chartId:      ChartId,
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
            chartId:      ChartId,
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

    /// <summary>
    /// Wraps the real issued-invoice enlister and throws AFTER it stages the Draft → Issued update but
    /// BEFORE the journal store's single SaveChangesAsync — the F3 crash window. Under the atomic fix the
    /// throw rolls BACK the staged JE + the staged status update together.
    /// </summary>
    private sealed class CrashingIssuedEnlister : INodeIssuedInvoiceWriteEnlister, Harborline.Api.Foundation.Coordination.IWriteEnlistment
    {
        private readonly INodeIssuedInvoiceWriteEnlister _inner;
        public CrashingIssuedEnlister(INodeIssuedInvoiceWriteEnlister inner) => _inner = inner;

        public Harborline.Api.Foundation.Coordination.WriteInvariant Invariant => NodeWriteInvariants.IssuedInvoice;

        public async ValueTask<Harborline.Api.Foundation.Coordination.WriteEnlistmentOutcome> EnlistAsync(
            Harborline.Api.Foundation.Coordination.StagedWriteUnitOfWork unitOfWork,
            CancellationToken cancellationToken = default)
        {
            await ((Harborline.Api.Foundation.Coordination.IWriteEnlistment)_inner)
                .EnlistAsync(unitOfWork, cancellationToken);
            throw new InvalidOperationException("SIMULATED CRASH @ JE→(Draft→Issued) window (ADR 0135 F3)");
        }

        public async Task EnlistIssuedInvoiceAsync(
            LocalNodeDbContext ctx, JournalEntry entry, CancellationToken ct = default)
        {
            await _inner.EnlistIssuedInvoiceAsync(ctx, entry, ct);
            // The JE row + the Draft→Issued update are BOTH staged on ctx now; the single SaveChangesAsync
            // has NOT run. A crash here must leave NEITHER persisted (atomic rollback) — the invoice stays
            // Draft and no JE lands.
            throw new InvalidOperationException("SIMULATED CRASH @ JE→(Draft→Issued) window (ADR 0135 F3)");
        }
    }
}
