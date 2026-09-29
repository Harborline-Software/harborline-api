using System.Data.Common;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Blocks.FinancialAp.Models;
using Harborline.Api.Blocks.FinancialAp.Services;
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
using Harborline.Api.LocalNodeHost.Tests.Authorization;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// DES-0029 kernel-core-ck-6 replay safety for the financial operations that commit their journal entry and
/// then, separately, their record (bill record/void, invoice void/write-off). A crash is injected at the real
/// record UPDATE after the journal entry committed; the retry must converge: one journal entry per source
/// reference, and the record pointing at the journal entry that actually exists.
/// </summary>
public sealed class JournalThenRecordRetryTests : IAsyncLifetime
{
    private static readonly TenantId Tenant = ActiveTeamTenantContext.ProjectTenantId(NodeTestActiveTeam.TestTeamId);
    private static readonly Harborline.Api.Foundation.Authorization.AuthorizationWriteContext Authority =
        TestAuthorization.Write(Tenant, "actor-1", new DateTimeOffset(2026, 3, 2, 9, 0, 0, TimeSpan.Zero));
    private static readonly ChartOfAccountsId Chart = new("CH-1");

    private readonly RecordUpdateFault _fault = new();
    private string _dir = null!;
    private IDbContextFactory<LocalNodeDbContext> _factory = null!;
    private JournalPostingService _journals = null!;
    private NodeEfJournalStore _journalStore = null!;

    public async Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "harborline-ck6-retry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var connectionString = $"Data Source={Path.Combine(_dir, "retry.db")};Pooling=False";
        var services = new ServiceCollection();
        services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialAp.Data.ApEntityModule>();
        services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialAr.Data.ArEntityModule>();
        services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialLedger.Data.FinancialLedgerEntityModule>();
        services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialPeriods.Data.FinancialPeriodsEntityModule>();
        services.AddSingleton<IHarborlineEntityModule, AuditEventEntityModule>();
        services.AddDbContextFactory<LocalNodeDbContext>(opt => opt.UseSqlite(connectionString).AddInterceptors(_fault));
        _factory = services.BuildServiceProvider().GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            await ctx.Database.EnsureCreatedAsync();
        }

        _journalStore = new NodeEfJournalStore(_factory, NodeJournalWriteAdapters.Create());
        _journals = new JournalPostingService(
            new NodeEfAccountResolver(_factory), new NodeEfPeriodResolver(_factory), _journalStore, TestAuthorization.AllowGate());
        await SeedAccountAsync("1100", GLAccountType.Asset, AccountSubtype.AccountsReceivable);
        await SeedAccountAsync("2000", GLAccountType.Liability, AccountSubtype.AccountsPayable);
        await SeedAccountAsync("4000", GLAccountType.Revenue, AccountSubtype.OperatingIncome);
        await SeedAccountAsync("6000", GLAccountType.Expense, AccountSubtype.OperatingExpense);
        await SeedAccountAsync("6900", GLAccountType.Expense, AccountSubtype.OperatingExpense);
        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            ctx.Set<FiscalPeriod>().Add(FiscalPeriod.CreateOpen(
                FiscalPeriodId.NewId(), Chart, new FiscalYearId("FY-2026"), FiscalPeriodKind.Monthly, "2026",
                new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), new Instant(Authority.At)));
            await ctx.SaveChangesAsync();
        }
    }

    public Task DisposeAsync()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
        return Task.CompletedTask;
    }

    [Fact(DisplayName = "ck-6 replay: a bill record retried after a crash between its JE and its record points at the one persisted JE")]
    public async Task BillRecord_RetryAfterCrash_PointsAtThePersistedEntry()
    {
        var service = Bills();
        await SeedDraftBillAsync();

        _fault.FailOn = "UPDATE \"bills\"";
        await Assert.ThrowsAnyAsync<Exception>(() => service.RecordAsync(BillId, Authority));
        var retry = await service.RecordAsync(BillId, Authority);

        Assert.True(retry.IsSuccess, retry.Detail);
        var persisted = await SingleEntryAsync($"bill:{BillId.Value}");
        Assert.Equal(persisted.Id, retry.PostedEntryId);
        Assert.Equal(persisted.Id, (await new NodeEfBillRepository(_factory).GetAsync(Tenant, BillId, Authority.At))!.JournalEntryId);
    }

    [Fact(DisplayName = "ck-6 replay: a bill void retried after a crash between its JE and its record points at the one persisted reversal")]
    public async Task BillVoid_RetryAfterCrash_PointsAtThePersistedEntry()
    {
        var service = Bills();
        await SeedDraftBillAsync();
        Assert.True((await service.RecordAsync(BillId, Authority)).IsSuccess);

        _fault.FailOn = "UPDATE \"bills\"";
        await Assert.ThrowsAnyAsync<Exception>(() => service.VoidAsync(BillId, "duplicate", Authority));
        var retry = await service.VoidAsync(BillId, "duplicate", Authority);

        Assert.True(retry.IsSuccess, retry.Detail);
        var persisted = await SingleEntryAsync($"bill-void:{BillId.Value}");
        Assert.Equal(persisted.Id, retry.ReversalEntryId);
        Assert.Equal(persisted.Id, (await new NodeEfBillRepository(_factory).GetAsync(Tenant, BillId, Authority.At))!.VoidedByEntryId);
    }

    [Fact(DisplayName = "ck-6 replay: an invoice void retried after a crash between its JE and its record points at the one persisted reversal")]
    public async Task InvoiceVoid_RetryAfterCrash_PointsAtThePersistedEntry()
    {
        var service = Invoices();
        await SeedIssuedInvoiceAsync(service);

        _fault.FailOn = "UPDATE \"invoices\"";
        await Assert.ThrowsAnyAsync<Exception>(() => service.VoidAsync(InvoiceId, "duplicate", Authority));
        var retry = await service.VoidAsync(InvoiceId, "duplicate", Authority);

        Assert.True(retry.IsSuccess, retry.Detail);
        var persisted = await SingleEntryAsync($"invoice-void:{InvoiceId.Value}");
        Assert.Equal(persisted.Id, retry.ReversalEntryId);
        Assert.Equal(persisted.Id, (await new NodeEfInvoiceRepository(_factory).GetAsync(Tenant, InvoiceId, Authority.At))!.VoidedByEntryId);
    }

    [Fact(DisplayName = "ck-6 replay: an invoice write-off retried after a crash between its JE and its record points at the one persisted entry")]
    public async Task InvoiceWriteOff_RetryAfterCrash_PointsAtThePersistedEntry()
    {
        var service = Invoices();
        await SeedIssuedInvoiceAsync(service);

        _fault.FailOn = "UPDATE \"invoices\"";
        await Assert.ThrowsAnyAsync<Exception>(() => service.WriteOffAsync(InvoiceId, new GLAccountId("6900"), "uncollectable", Authority));
        var retry = await service.WriteOffAsync(InvoiceId, new GLAccountId("6900"), "uncollectable", Authority);

        Assert.True(retry.IsSuccess, retry.Detail);
        var persisted = await SingleEntryAsync($"invoice-writeoff:{InvoiceId.Value}");
        Assert.Equal(persisted.Id, retry.BadDebtEntryId);
        Assert.Equal(persisted.Id, (await new NodeEfInvoiceRepository(_factory).GetAsync(Tenant, InvoiceId, Authority.At))!.WrittenOffByEntryId);
    }

    // ── composition + helpers ─────────────────────────────────────────────────

    private static readonly BillId BillId = new("22222222-2222-2222-2222-222222222222");
    private static readonly InvoiceId InvoiceId = new("33333333-3333-3333-3333-333333333333");

    private BillPostingService Bills() => new(
        tenantContext: new ActiveTeamTenantContext(NodeTestActiveTeam.Accessor),
        bills: new NodeEfBillRepository(_factory),
        tax: new Harborline.Api.Blocks.FinancialAp.Services.NoOpTaxCalculator(),
        journals: _journals,
        timeProvider: TimeProvider.System);

    private InvoicePostingService Invoices() => new(
        tenantContext: new ActiveTeamTenantContext(NodeTestActiveTeam.Accessor),
        invoices: new NodeEfInvoiceRepository(_factory),
        numbering: new NodeEfInvoiceNumberingService(_factory, new ReplicaId("AA")),
        tax: new Harborline.Api.Blocks.FinancialAr.Services.NoOpTaxCalculator(),
        journals: _journals,
        events: null,
        journalStore: _journalStore,
        timeProvider: TimeProvider.System);

    private async Task<JournalEntry> SingleEntryAsync(string sourceReference)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        return await ctx.Set<JournalEntry>().AsNoTracking()
            .SingleAsync(e => e.TenantId == Tenant && e.SourceReference == sourceReference);
    }

    private async Task SeedDraftBillAsync()
    {
        var line = BillLine.Create(BillId, 1, "Repairs", 1m, 120m, new GLAccountId("6000"));
        var bill = Bill.Create(
            tenantId: Tenant,
            chartId: Chart,
            billNumber: "BILL-0001",
            vendorId: new PartyId("vendor-1"),
            billDate: new DateOnly(2026, 3, 1),
            dueDate: new DateOnly(2026, 3, 31),
            lines: new[] { line },
            apAccountId: new GLAccountId("2000"),
            createdAtUtc: new Instant(Authority.At),
            id: BillId);
        await new NodeEfBillRepository(_factory).UpsertAsync(Tenant, bill, Authority.At);
    }

    private async Task SeedIssuedInvoiceAsync(InvoicePostingService service)
    {
        var line = InvoiceLine.Create(InvoiceId, 1, "Service", 1m, 250m, new GLAccountId("4000"));
        var draft = Invoice.Create(
            tenantId: Tenant,
            chartId: Chart,
            invoiceNumber: "INV-2026-03-01-AA-0001",
            customerId: new PartyId("customer-1"),
            issueDate: new DateOnly(2026, 3, 1),
            dueDate: new DateOnly(2026, 3, 31),
            lines: new[] { line },
            arAccountId: new GLAccountId("1100"),
            createdAtUtc: new Instant(Authority.At),
            createdBy: new PartyId("actor-1"),
            id: InvoiceId);
        await new NodeEfInvoiceRepository(_factory).UpsertAsync(Tenant, draft, Authority.At);
        var issued = await service.IssueAsync(InvoiceId, Authority);
        Assert.True(issued.IsSuccess, issued.Detail);
    }

    private async Task SeedAccountAsync(string code, GLAccountType type, AccountSubtype subtype)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        ctx.Set<GLAccount>().Add(GLAccount.Create(
            id: new GLAccountId(code), chartId: Chart, code: code, name: code, type: type, subtype: subtype,
            currency: "USD", isPostable: true, createdAtUtc: new Instant(Authority.At)));
        await ctx.SaveChangesAsync();
    }

    /// <summary>Throws before the first SQL command containing <see cref="FailOn"/>, once: the crash point.</summary>
    private sealed class RecordUpdateFault : DbCommandInterceptor
    {
        public string? FailOn { get; set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Check(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Check(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Check(DbCommand command)
        {
            if (FailOn is { } marker && command.CommandText.Contains(marker, StringComparison.Ordinal))
            {
                FailOn = null;
                throw new InvalidOperationException($"ck-6 injected crash before: {marker}");
            }
        }
    }
}
