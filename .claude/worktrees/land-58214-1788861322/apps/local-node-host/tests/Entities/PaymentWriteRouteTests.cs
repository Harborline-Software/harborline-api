using System.Net;
using Harborline.Api.Kernel.Runtime.Teams;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

using Harborline.Api.Blocks.FinancialAr.Models;
using Harborline.Api.Blocks.FinancialAr.Services;
using Harborline.Api.Blocks.FinancialAp.Services;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialLedger.Services;
using Harborline.Api.Blocks.FinancialPayments.Models;
using Harborline.Api.Blocks.FinancialPayments.Services;
using Harborline.Api.Blocks.Leases.Services;
using Harborline.Api.Blocks.FinancialSubLedger.Models;
using Harborline.Api.Blocks.FinancialSubLedger.Services;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.MultiTenancy;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Health;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// Route-level tests for the node-local payment-WRITE surface (<see cref="PaymentWriteRoutes"/>) — the
/// ADR 0122 §D4 T2 node payment-WRITE residency, hardened by PPI-1 so an uncleared payment remains
/// visibly not cleared and cannot reduce an invoice/bill or ADR-0120 sub-ledger position.
/// </summary>
/// <remarks>
/// <para>
/// Hosts the SAME routes the production endpoints register (payment-write + invoice-write +
/// lease-sub-ledger read), composed via the PRODUCTION extension methods
/// (<c>AddNodeInvoiceWrites</c> / <c>AddNodeBillWrites</c> / <c>AddNodePaymentWrites</c> /
/// <c>AddNodeLeaseSubLedgerReads</c>) on a real in-process Kestrel listener over a temp SQLite store,
/// driven by a real <see cref="HttpClient"/>. So recording a payment really writes the Payment into
/// the recoverable store while the missing clearing journal prevents a PaymentApplication and open-item
/// mutation; the bug-2849 outer-container wiring is exercised by running the composed host.
/// </para>
/// </remarks>
public sealed class PaymentWriteRouteTests : IAsyncLifetime
{
    private static readonly DateTimeOffset FixedNow =
        DateTimeOffset.Parse("2026-07-16T17:30:00Z", System.Globalization.CultureInfo.InvariantCulture);

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private string _dir = null!;
    private IDbContextFactory<LocalNodeDbContext> _factory = null!;

    // ADR 0032: the route's data tenant now follows the active team — so the seed tenant must be the
    // active team's projected tenant (NOT the retired "local" literal) for the route filters to match.
    private static readonly TenantId LocalTenantId =
        Harborline.Api.LocalNodeHost.Data.Financial.ActiveTeamTenantContext.ProjectTenantId(NodeTestActiveTeam.TestTeamId);

    private const string InvoicesRoute = "/api/local-node/invoices";
    private const string LeaseRoute = "/api/local-node/leases";

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        _dir = Path.Combine(Path.GetTempPath(), "harborline-payment-write-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var connectionString = $"Data Source={Path.Combine(_dir, "payment-write-test.db")}";

        // The shared entity modules the production LocalNodeDbContext composes (Ar/Ap/Payments/Ledger).
        builder.Services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialAr.Data.ArEntityModule>();
        builder.Services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialAp.Data.ApEntityModule>();
        builder.Services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialPayments.Data.PaymentsEntityModule>();
        builder.Services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialLedger.Data.FinancialLedgerEntityModule>();
        builder.Services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialPeriods.Data.FinancialPeriodsEntityModule>();
        builder.Services.AddSingleton<IHarborlineEntityModule, Harborline.Api.LocalNodeHost.Data.Audit.AuditEventEntityModule>();
        builder.Services.AddDbContextFactory<LocalNodeDbContext>(opt => opt.UseSqlite(connectionString));

        foreach (var enlistment in NodeJournalWriteAdapters.Create()
                     .Where(candidate => candidate.Invariant != NodeWriteInvariants.IssuedInvoice))
        {
            builder.Services.AddSingleton(enlistment);
        }

        // ADR 0032 identity layer: the financial write services' ambient ITenantContext is now
        // active-team-derived (ActiveTeamTenantContext). Supply a fixed active team so the tenant resolves.
        builder.Services.AddTestActiveTeam();

        // Production composition: posting + AR/AP writes + payment writes + lease sub-ledger reads.
        builder.Services.AddSingleton<NodeEfJournalStore>();
        builder.Services.AddSingleton<IJournalStore>(sp => sp.GetRequiredService<NodeEfJournalStore>());
        builder.Services.AddNodeFinancialPosting();
        builder.Services.AddNodeBillWrites();
        builder.Services.AddNodeInvoiceWrites();
        builder.Services.AddNodePaymentWrites();
        builder.Services.RemoveAll<IPaymentApplicationService>();
        builder.Services.AddSingleton<IPaymentApplicationService>(sp =>
            new DefaultPaymentApplicationService(
                payments: sp.GetRequiredService<IPaymentRepository>(),
                applications: sp.GetRequiredService<IPaymentApplicationRepository>(),
                invoices: sp.GetRequiredService<IInvoiceRepository>(),
                bills: sp.GetRequiredService<IBillRepository>(),
                tenantContext: sp.GetRequiredService<ITenantContext>(),
                // Resolved from the production graph (AddNodeFinancialPosting registers both), so
                // this route test exercises the same reversal period gate production does.
                periods: sp.GetRequiredService<Harborline.Api.Blocks.FinancialLedger.Services.IPeriodResolver>(),
                // No gate: every period this route test acts in is Open, so the soft-close override is
                // never reached. A container without a gate REFUSES the override (ticket 205 slice 5), so
                // omitting it here cannot open anything; the override's own contract lives in
                // PaymentApplicationUnapplyServiceSeamTests.
                gate: null,
                events: sp.GetRequiredService<Harborline.Api.Foundation.Events.IDomainEventPublisher>(),
                timeProvider: new FixedTimeProvider(FixedNow)));
        builder.Services.AddNodeLeaseSubLedgerReads();

        _app = builder.Build();
        _factory = _app.Services.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            await ctx.Database.EnsureCreatedAsync();

            // An OPEN fiscal period covering the fixed clock. Unapply gates on the period covering
            // the reversal date, so without this the production graph refuses every unapply with
            // NoPeriodForReversalDate — which is the gate working, not a fixture accident. The
            // Locked twin of this seed is asserted in UnapplyInvoicePayment_IsRefused_WhenTheReversalPeriodIsLocked.
            ctx.Set<Harborline.Api.Blocks.FinancialPeriods.Models.FiscalPeriod>().Add(
                Harborline.Api.Blocks.FinancialPeriods.Models.FiscalPeriod.CreateOpen(
                    id: new FiscalPeriodId("period:payment-write-tests"),
                    chartId: new ChartOfAccountsId("CH-1"),
                    fiscalYearId: new Harborline.Api.Blocks.FinancialPeriods.Models.FiscalYearId("fy:2026"),
                    kind: Harborline.Api.Blocks.FinancialPeriods.Models.FiscalPeriodKind.Monthly,
                    label: "2026-07",
                    startDate: new DateOnly(2026, 7, 1),
                    endDate: new DateOnly(2026, 7, 31),
                    createdAtUtc: new Instant(FixedNow)));
            await ctx.SaveChangesAsync();
        }

        // Map the SAME production routes (payment-write + lease sub-ledger read).
        _app.Use(async (http, next) =>
        {
            http.Features.Set(DesktopPlaneRequestFeature.Instance);
            await next(http);
        });
        var deviceReachable = _app.MapDeviceReachableProductDataGroup();
        PaymentWriteRoutes.Map(
            deviceReachable,
            (
                _app.Services.GetRequiredService<NodeEfPaymentRepository>(),
                _app.Services.GetRequiredService<NodeEfPaymentApplicationRepository>(),
                _app.Services.GetRequiredService<IPaymentApplicationService>()),
            _app.Services.GetRequiredService<NodeEfInvoiceRepository>(),
            _app.Services.GetRequiredService<NodeEfBillRepository>(),
            _app.Services.GetRequiredService<IActiveTeamAccessor>(),
            TimeProvider.System);
        LeaseSubLedgerRoutes.Map(
            deviceReachable,
            _app.Services.GetRequiredService<ILeaseSubLedgerLinkRepository>(),
            _app.Services.GetRequiredService<ISubLedgerReadModel>(),
            _app.Services.GetRequiredService<LeaseSubLedgerService>(),
            _app.Services.GetRequiredService<IActiveTeamAccessor>(),
            TimeProvider.System);

        await _app.StartAsync();
        var addr = _app.Services
            .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!
            .Addresses.First();
        _client = new HttpClient { BaseAddress = new Uri(addr) };
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        try { if (_dir is not null) Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    // Seed an Issued invoice stamped with a sub-ledger FK (so it joins the lease's history).
    private async Task SeedIssuedInvoiceAsync(
        string id, string chartId, string customerId, string number, DateOnly issueDate, decimal total,
        SubLedgerAccountId subLedger)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        var line = InvoiceLine.Create(
            invoiceId: new InvoiceId(id), lineNumber: 1, description: "Rent",
            quantity: 1m, unitPrice: total, incomeAccountId: new GLAccountId("4000"));
        var invoice = Invoice.Create(
            tenantId: LocalTenantId, chartId: new ChartOfAccountsId(chartId),
            invoiceNumber: number, customerId: new Harborline.Api.Blocks.People.Foundation.Models.PartyId(customerId),
            issueDate: issueDate, dueDate: issueDate.AddDays(30),
            lines: new[] { line }, arAccountId: new GLAccountId("1100"),
            createdAtUtc: new Instant(System.TimeProvider.System.GetUtcNow()),
            id: new InvoiceId(id), subLedgerAccountId: subLedger) with
        {
            Status = InvoiceStatus.Issued,
        };
        ctx.Set<Invoice>().Add(invoice);
        await ctx.SaveChangesAsync();
    }

    private async Task<string> ActivateAsync(string leaseName, string chartId, string customerPartyId)
    {
        var resp = await _client.PostAsJsonAsync(
            $"{LeaseRoute}/{leaseName}/activate-subledger",
            new ActivateLeaseSubLedgerRequest(
                ChartId: chartId, ArControlAccountId: "1100", CustomerPartyId: customerPartyId));
        resp.EnsureSuccessStatusCode();
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return doc.GetProperty("subLedgerAccountId").GetString()!;
    }

    // ── Tests ───────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "PPI-1: record without clearing JE stays visibly not cleared and leaves invoice open")]
    public async Task RecordInvoicePayment_WithoutClearingJournal_PersistsWithoutApplying()
    {
        var accountId = new SubLedgerAccountId(await ActivateAsync("LEASE-PW1", "CH-1", "CUST-1"));
        await SeedIssuedInvoiceAsync("INV-PW1", "CH-1", "CUST-1", "INV-2026-01-01-AA-9001",
            new DateOnly(2026, 1, 1), 1500m, accountId);

        var post = await _client.PostAsJsonAsync(
            $"{InvoicesRoute}/INV-PW1/payments",
            new RecordNodePaymentRequest(
                Amount: 1500m, Currency: "USD", Method: "ACH", PaymentDate: "2026-01-15",
                Reference: "CHK-1001"));
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);

        var created = await post.Content.ReadFromJsonAsync<JsonElement>();
        var paymentRow = created.GetProperty("data");
        Assert.Equal("Draft", paymentRow.GetProperty("status").GetString());
        Assert.Equal("recorded_not_cleared", paymentRow.GetProperty("clearingState").GetString());

        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            var invoice = await ctx.Set<Invoice>().AsNoTracking().SingleAsync(x => x.Id == new InvoiceId("INV-PW1"));
            Assert.Equal(InvoiceStatus.Issued, invoice.Status);
            Assert.Equal(0m, invoice.AmountPaid);
            Assert.Equal(1500m, invoice.Balance);

            var payment = await ctx.Set<Payment>().AsNoTracking().SingleAsync();
            Assert.Equal(PaymentStatus.Draft, payment.Status);
            Assert.Null(payment.JournalEntryId);
            Assert.Equal(AppliedTo.Invoice, payment.IntendedTargetType);
            Assert.Equal("INV-PW1", payment.IntendedTargetId);
            Assert.Empty(await ctx.Set<PaymentApplication>().AsNoTracking().ToListAsync());
        }

        var list = await _client.GetFromJsonAsync<JsonElement>($"{InvoicesRoute}/INV-PW1/payments");
        Assert.Equal(0, list.GetProperty("data").GetArrayLength());
    }

    [Fact(DisplayName = "Payment write: an unapplied invoice payment disappears from the applied list")]
    public async Task UnapplyInvoicePayment_RemovesItFromAppliedList()
    {
        var accountId = new SubLedgerAccountId(await ActivateAsync("LEASE-PW5", "CH-1", "CUST-5"));
        await SeedIssuedInvoiceAsync("INV-PW5", "CH-1", "CUST-5", "INV-2026-01-01-AA-9005",
            new DateOnly(2026, 1, 1), 500m, accountId);

        // Reversal presupposes an applied payment. Seed the clearing-path state explicitly: the
        // payment carries a bank account and clearing journal, then the production application
        // service applies it before the route's applied-list behavior is exercised.
        var paymentServices = (
            Payments: _app.Services.GetRequiredService<NodeEfPaymentRepository>(),
            ApplicationRepository: _app.Services.GetRequiredService<NodeEfPaymentApplicationRepository>(),
            Applications: _app.Services.GetRequiredService<IPaymentApplicationService>());
        var actor = new Harborline.Api.Blocks.People.Foundation.Models.PartyId("party:test-operator");
        var payment = Payment.Create(
            tenantId: LocalTenantId,
            chartId: new ChartOfAccountsId("CH-1"),
            direction: PaymentDirection.Inbound,
            paymentNumber: "PAY-PW5",
            partyId: new Harborline.Api.Blocks.People.Foundation.Models.PartyId("CUST-5"),
            paymentDate: new DateOnly(2026, 1, 15),
            amount: 500m,
            method: PaymentMethod.ACH,
            bankAccountId: new GLAccountId("1000"),
            createdAtUtc: new Instant(FixedNow)) with
        {
            Status = PaymentStatus.Unapplied,
            JournalEntryId = new JournalEntryId("JE-PAY-PW5-CLEAR"),
            UpdatedAtUtc = new Instant(FixedNow),
        };
        await paymentServices.Payments.AddAsync(LocalTenantId, payment, new Instant(System.TimeProvider.System.GetUtcNow()).Value);

        var applied = await paymentServices.Applications.ApplyAsync(
            payment.Id,
            AppliedTo.Invoice,
            "INV-PW5",
            500m,
            0m,
            0m,
            actor);
        Assert.Equal(ApplyError.None, applied.Error);

        var before = await _client.GetFromJsonAsync<JsonElement>($"{InvoicesRoute}/INV-PW5/payments");
        Assert.Equal(1, before.GetProperty("data").GetArrayLength());

        var application = Assert.Single(
            await paymentServices.ApplicationRepository.ListByTargetAsync(
                LocalTenantId, "INV-PW5", CancellationToken.None));

        var unapplied = await paymentServices.Applications.UnapplyAsync(
            application.Id,
            actor,
            new Harborline.Api.Foundation.Authorization.AuthorizationWriteContext(
                new Harborline.Api.Foundation.Assets.Common.ActorId("user:payment-write-route-test"),
                LocalTenantId,
                FixedNow));
        Assert.True(unapplied.Success);

        var retained = await paymentServices.ApplicationRepository.ListByTargetAsync(
            LocalTenantId, "INV-PW5", CancellationToken.None);
        Assert.Equal(2, retained.Count);
        Assert.All(retained, row => Assert.False(row.IsActive));
        Assert.Equal(
            FixedNow,
            retained.Single(row => row.Id == application.Id).ReversedAtUtc!.Value.Value);

        var after = await _client.GetFromJsonAsync<JsonElement>($"{InvoicesRoute}/INV-PW5/payments");
        Assert.Empty(after.GetProperty("data").EnumerateArray());
    }

    [Fact(DisplayName = "PPI-1: record without clearing JE preserves ADR-0120 R1 immediately")]
    public async Task RecordInvoicePayment_WithoutClearingJournal_PreservesR1()
    {
        var accountId = new SubLedgerAccountId(await ActivateAsync("LEASE-PW2", "CH-1", "CUST-2"));
        await SeedIssuedInvoiceAsync("INV-PW2", "CH-1", "CUST-2", "INV-2026-01-01-AA-9002",
            new DateOnly(2026, 1, 1), 1500m, accountId);

        var fixedInstant = new Instant(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        var invoiceJournal = new JournalEntry(
            id: new JournalEntryId("JE-INV-PW2"),
            tenantId: LocalTenantId,
            entryDate: new DateOnly(2026, 1, 1),
            memo: "Issue INV-PW2",
            lines:
            [
                new JournalEntryLine(new GLAccountId("1100"), debit: 1500m, credit: 0m),
                new JournalEntryLine(new GLAccountId("4000"), debit: 0m, credit: 1500m),
            ],
            createdAtUtc: fixedInstant,
            sourceReference: "invoice:INV-PW2")
        {
            ChartId = new ChartOfAccountsId("CH-1"),
            Status = JournalEntryStatus.Posted,
            PostedAtUtc = fixedInstant,
            SourceKind = JournalEntrySource.Invoice,
        };
        var journalStore = _app.Services.GetRequiredService<IJournalStore>();
        await journalStore.SaveAtomicForTestAsync(LocalTenantId, invoiceJournal);

        // Record a 1500 inbound payment. With no BankAccountId there can be no clearing JE and PPI-1
        // must keep the invoice balance unchanged.
        var post = await _client.PostAsJsonAsync(
            $"{InvoicesRoute}/INV-PW2/payments",
            new RecordNodePaymentRequest(
                Amount: 1500m, Currency: "USD", Method: "ACH", PaymentDate: "2026-01-15"));
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);

        var created = await post.Content.ReadFromJsonAsync<JsonElement>();
        var paymentId = created.GetProperty("data").GetProperty("id").GetString()!;
        Assert.Null(await journalStore.FindBySourceReferenceAsync(
            LocalTenantId, $"payment-clear:{paymentId}"));

        var asOf = new DateOnly(2026, 1, 31);
        var position = await _app.Services.GetRequiredService<ISubLedgerReadModel>()
            .GetPositionAsync(LocalTenantId, accountId, asOf);
        var glArBalance = journalStore.Snapshot(LocalTenantId)
            .Where(entry => entry.Status == JournalEntryStatus.Posted && entry.EntryDate <= asOf)
            .SelectMany(entry => entry.Lines)
            .Where(line => line.AccountId == new GLAccountId("1100"))
            .Sum(line => line.Debit - line.Credit);

        Assert.Equal(1500m, position.Balance);
        Assert.Equal(position.Balance, glArBalance);
    }

    [Fact(DisplayName = "Payment write: re-driven record with the same sourceReference is idempotent (no duplicate)")]
    public async Task RecordInvoicePayment_IdempotentOnSourceReference()
    {
        var accountId = new SubLedgerAccountId(await ActivateAsync("LEASE-PW3", "CH-1", "CUST-3"));
        await SeedIssuedInvoiceAsync("INV-PW3", "CH-1", "CUST-3", "INV-2026-01-01-AA-9003",
            new DateOnly(2026, 1, 1), 1500m, accountId);

        var body = new RecordNodePaymentRequest(
            Amount: 500m, Currency: "USD", Method: "ACH", PaymentDate: "2026-01-15",
            SourceReference: "idem-key-PW3");

        var first = await _client.PostAsJsonAsync($"{InvoicesRoute}/INV-PW3/payments", body);
        var second = await _client.PostAsJsonAsync($"{InvoicesRoute}/INV-PW3/payments", body);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        // Exactly one uncleared payment recorded despite two submits; neither submit applies it.
        await using var ctx = await _factory.CreateDbContextAsync();
        Assert.Equal(1, await ctx.Set<Payment>().CountAsync());
        Assert.Equal(0, await ctx.Set<PaymentApplication>().CountAsync());
    }

    [Fact(DisplayName = "Payment write: recording against an unknown invoice is 404")]
    public async Task RecordInvoicePayment_UnknownInvoice_NotFound()
    {
        var post = await _client.PostAsJsonAsync(
            $"{InvoicesRoute}/INV-NOPE/payments",
            new RecordNodePaymentRequest(
                Amount: 100m, Currency: "USD", Method: "Cash", PaymentDate: "2026-01-15"));
        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
    }

    [Fact(DisplayName = "Payment write: a negative amount is rejected (400)")]
    public async Task RecordInvoicePayment_NegativeAmount_BadRequest()
    {
        var accountId = new SubLedgerAccountId(await ActivateAsync("LEASE-PW4", "CH-1", "CUST-4"));
        await SeedIssuedInvoiceAsync("INV-PW4", "CH-1", "CUST-4", "INV-2026-01-01-AA-9004",
            new DateOnly(2026, 1, 1), 1500m, accountId);

        var post = await _client.PostAsJsonAsync(
            $"{InvoicesRoute}/INV-PW4/payments",
            new RecordNodePaymentRequest(
                Amount: -50m, Currency: "USD", Method: "ACH", PaymentDate: "2026-01-15"));
        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
