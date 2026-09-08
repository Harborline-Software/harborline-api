using System.Net;
using Harborline.Api.Kernel.Runtime.Teams;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Harborline.Api.Blocks.FinancialAr.Models;
using Harborline.Api.Blocks.FinancialAr.Services;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialPayments.Models;
using Harborline.Api.Blocks.FinancialSubLedger.Models;
using Harborline.Api.Blocks.FinancialSubLedger.Services;
using Harborline.Api.Blocks.Leases.Services;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Blocks.FinancialAp.Data;
using Harborline.Api.Blocks.FinancialAr.Data;
using Harborline.Api.Blocks.FinancialLedger.Data;
using Harborline.Api.Blocks.FinancialPayments.Data;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.MultiTenancy;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Health;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// Route-level tests for the node-local lease sub-ledger surface (<see cref="LeaseSubLedgerRoutes"/>) —
/// the ADR 0122 §D4 P2 → (b) offline lease payment-history read path.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the P2 acceptance proof in test form.</b> The binding acceptance is "lease payment-history
/// renders REAL data with signal-bridge STOPPED." These tests host the SAME route handlers
/// <see cref="HostedLeaseSubLedgerApiEndpoint"/> registers, over the SAME production composition
/// (<see cref="NodeLeaseSubLedgerComposition.AddNodeLeaseSubLedgerReads"/> + the node AR/AP/payment
/// repos it composes over), on a real in-process Kestrel listener backed by a temp SQLite store — with
/// NO Bridge anywhere in the graph. They seed node-resident invoices (charges) + payments into
/// <c>local-node.db</c>, activate the lease's sub-ledger link in-process, and assert the
/// payment-history endpoint returns the real chronological ledger (charges + payments + running
/// balance). If these pass, the offline read chain renders real data.
/// </para>
/// <para>
/// The store is plain (unencrypted) SQLite here — SC-1 SQLCipher encryption is covered separately in
/// the encryption tests; this surface's concern is the read chain, scoped correctly.
/// </para>
/// </remarks>
public sealed class LeaseSubLedgerRouteTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private string _dir = null!;
    private IDbContextFactory<LocalNodeDbContext> _factory = null!;

    // ADR 0032: the route's data tenant now follows the active team — so the seed tenant must be the
    // active team's projected tenant (NOT the retired "local" literal) for the route filters to match.
    private static readonly TenantId LocalTenantId =
        Harborline.Api.LocalNodeHost.Data.Financial.ActiveTeamTenantContext.ProjectTenantId(NodeTestActiveTeam.TestTeamId);

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        _dir = Path.Combine(Path.GetTempPath(), "harborline-lease-subledger-routes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var connectionString = $"Data Source={Path.Combine(_dir, "lease-subledger-test.db")};Pooling=False";

        // Compose the SAME entity modules the production host does for the financial surfaces under
        // test: ledger (ChartOfAccounts/LegalEntity), AR (invoices), AP (bills), payments.
        builder.Services.AddSingleton<IHarborlineEntityModule, FinancialLedgerEntityModule>();
        builder.Services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialPeriods.Data.FinancialPeriodsEntityModule>();
        builder.Services.AddSingleton<IHarborlineEntityModule, ArEntityModule>();
        builder.Services.AddSingleton<IHarborlineEntityModule, ApEntityModule>();
        builder.Services.AddSingleton<IHarborlineEntityModule, PaymentsEntityModule>();
        builder.Services.AddDbContextFactory<LocalNodeDbContext>(opt => opt.UseSqlite(connectionString));

        // ADR 0032 identity layer: the lease routes resolve the data tenant from the active team.
        builder.Services.AddTestActiveTeam();

        // The node AR/AP repositories the SubLedgerReadModel composes over. In production these come
        // from AddNodeInvoiceWrites / AddNodeBillWrites; here we register just the recoverable repos
        // (the read-model only needs the repositories, not the full posting slice).
        builder.Services.AddSingleton<NodeEfInvoiceRepository>();
        builder.Services.AddSingleton<IInvoiceRepository>(sp => sp.GetRequiredService<NodeEfInvoiceRepository>());
        builder.Services.AddSingleton<NodeEfBillRepository>();
        builder.Services.AddSingleton<Harborline.Api.Blocks.FinancialAp.Services.IBillRepository>(
            sp => sp.GetRequiredService<NodeEfBillRepository>());

        // The production lease sub-ledger READ composition under test (read-model + account repo +
        // node payment repos + lease link/activation services).
        builder.Services.AddNodeLeaseSubLedgerReads();

        _app = builder.Build();

        _factory = _app.Services.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            await ctx.Database.EnsureCreatedAsync();
        }

        // Map the SAME production routes (single source of truth, mirrors HostedLeaseSubLedgerApiEndpoint).
        _app.Use(async (http, next) =>
        {
            http.Features.Set(DesktopPlaneRequestFeature.Instance);
            await next(http);
        });
        LeaseSubLedgerRoutes.Map(
            _app.MapDeviceReachableProductDataGroup(),
            _app.Services.GetRequiredService<ILeaseSubLedgerLinkRepository>(),
            _app.Services.GetRequiredService<ISubLedgerReadModel>(),
            _app.Services.GetRequiredService<LeaseSubLedgerService>(),
            _app.Services.GetRequiredService<IActiveTeamAccessor>(),
            TimeProvider.System);

        await _app.StartAsync();

        var addresses = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>();
        _client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) };
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    private const string Base = "/api/local-node/leases";

    // ── Fixture seeding (direct durable-row insert — models rows already committed node-side) ──

    /// <summary>
    /// Inserts an AR invoice directly into the node <c>invoices</c> table, stamped with
    /// <paramref name="subLedgerAccountId"/> (the lease's sub-ledger FK). Models an issued rent invoice
    /// the node write-path has already committed.
    /// </summary>
    private async Task SeedInvoiceAsync(
        string id,
        string chartId,
        string customerId,
        string number,
        DateOnly issueDate,
        decimal amount,
        SubLedgerAccountId subLedgerAccountId,
        InvoiceStatus status = InvoiceStatus.Issued)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        var line = InvoiceLine.Create(
            invoiceId:       new InvoiceId(id),
            lineNumber:      1,
            description:     "Rent",
            quantity:        1m,
            unitPrice:       amount,
            incomeAccountId: new GLAccountId("4000"));
        var invoice = Invoice.Create(
            tenantId:           LocalTenantId,
            chartId:            new ChartOfAccountsId(chartId),
            invoiceNumber:      number,
            customerId:         new PartyId(customerId),
            issueDate:          issueDate,
            dueDate:            issueDate.AddDays(30),
            lines:              new[] { line },
            arAccountId:        new GLAccountId("1100"),
            createdAtUtc:       new Instant(System.TimeProvider.System.GetUtcNow()),
            id:                 new InvoiceId(id),
            subLedgerAccountId: subLedgerAccountId) with
        {
            Status = status,
        };
        ctx.Set<Invoice>().Add(invoice);
        await ctx.SaveChangesAsync();
    }

    /// <summary>
    /// Inserts an inbound Payment directly into the node <c>payments</c> table with an application
    /// against <paramref name="targetInvoiceId"/>. Models a rent payment the posting path has committed.
    /// </summary>
    private async Task SeedPaymentAsync(
        string id,
        string chartId,
        string partyId,
        string number,
        DateOnly date,
        decimal amount,
        string targetInvoiceId)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        var application = new PaymentApplication
        {
            Id = new PaymentApplicationId(id + "-APP-1"),
            TenantId = LocalTenantId,
            PaymentId = new PaymentId(id),
            AppliedTo = AppliedTo.Invoice,
            TargetId = targetInvoiceId,
            AmountApplied = amount,
            AppliedDate = date,
            CreatedAtUtc = new Instant(System.TimeProvider.System.GetUtcNow()),
        };
        var payment = new Payment
        {
            Id = new PaymentId(id),
            TenantId = LocalTenantId,
            ChartId = new ChartOfAccountsId(chartId),
            Direction = PaymentDirection.Inbound,
            PaymentNumber = number,
            PartyId = new PartyId(partyId),
            PaymentDate = date,
            Amount = amount,
            UnappliedAmount = 0m,
            Currency = "USD",
            Method = PaymentMethod.ACH,
            Status = PaymentStatus.Applied,
            // Snapshot kept for read-back convenience; the read-model now resolves the payment-leg from
            // the AUTHORITATIVE PaymentApplication rows (ADR-0122 C-T2-1), so we persist the application
            // row to the table too (below) — not just the JSONB snapshot.
            Applications = new[] { application },
            CreatedAtUtc = new Instant(System.TimeProvider.System.GetUtcNow()),
            UpdatedAtUtc = new Instant(System.TimeProvider.System.GetUtcNow()),
            Version = 1,
        };
        ctx.Set<Payment>().Add(payment);
        // C-T2-1: persist the authoritative PaymentApplication row — SubLedgerReadModel.GetHistoryAsync
        // reads applications via IPaymentApplicationRepository.ListByTargetAsync (FK-scoped), not the
        // non-authoritative Payment.Applications snapshot.
        ctx.Set<PaymentApplication>().Add(application);
        await ctx.SaveChangesAsync();
    }

    /// <summary>
    /// Activates the lease's sub-ledger via the production endpoint and returns the minted account id.
    /// </summary>
    private async Task<string> ActivateAsync(string leaseName, string chartId, string customerPartyId)
    {
        var resp = await _client.PostAsJsonAsync(
            $"{Base}/{leaseName}/activate-subledger",
            new ActivateLeaseSubLedgerRequest(
                ChartId: chartId,
                ArControlAccountId: "1100", // Asset-class AR control (Kind-guard requires Asset)
                CustomerPartyId: customerPartyId));
        resp.EnsureSuccessStatusCode();
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return doc.GetProperty("subLedgerAccountId").GetString()!;
    }

    // ── Tests ───────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "Lease history: un-activated lease returns empty 200 (not 404)")]
    public async Task History_UnactivatedLease_EmptyOk()
    {
        var doc = await _client.GetFromJsonAsync<JsonElement>($"{Base}/LEASE-NOPE/payment-history");
        Assert.Equal(JsonValueKind.Null, doc.GetProperty("subLedgerAccountId").ValueKind);
        Assert.Equal(0, doc.GetProperty("data").GetArrayLength());
    }

    [Fact(DisplayName = "Lease history: activate is idempotent (same account id on re-activate)")]
    public async Task Activate_Idempotent()
    {
        var first = await ActivateAsync("LEASE-0001", "CH-1", "PARTY-1");
        var second = await ActivateAsync("LEASE-0001", "CH-1", "PARTY-1");
        Assert.Equal(first, second);
    }

    [Fact(DisplayName = "Lease history: activate rejects a non-Asset AR control account (Kind-guard)")]
    public async Task Activate_NonAssetControl_Rejected()
    {
        // The activate route always passes Asset to the Kind-guard for the AR sub-ledger, so the guard
        // itself cannot be tripped via the wire; this asserts a missing control account is a 400.
        var resp = await _client.PostAsJsonAsync(
            $"{Base}/LEASE-X/activate-subledger",
            new ActivateLeaseSubLedgerRequest(ChartId: "CH-1", ArControlAccountId: "", CustomerPartyId: "PARTY-1"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact(DisplayName = "Lease history: renders REAL node-resident charge data offline (the P2 acceptance proof)")]
    public async Task History_RendersRealChargeData()
    {
        // Activate the lease's sub-ledger (mint account + link).
        var accountId = await ActivateAsync("LEASE-0001", "CH-1", "PARTY-1");
        var subLedger = new SubLedgerAccountId(accountId);

        // Seed two issued rent invoices stamped with the lease's sub-ledger FK (node-resident charges).
        await SeedInvoiceAsync("INV-1", "CH-1", "PARTY-1", "INV-2026-01-01-AA-0001",
            new DateOnly(2026, 1, 1), 1500m, subLedger);
        await SeedInvoiceAsync("INV-2", "CH-1", "PARTY-1", "INV-2026-02-01-AA-0002",
            new DateOnly(2026, 2, 1), 1500m, subLedger);

        var doc = await _client.GetFromJsonAsync<JsonElement>($"{Base}/LEASE-0001/payment-history");

        Assert.Equal(accountId, doc.GetProperty("subLedgerAccountId").GetString());
        var data = doc.GetProperty("data");
        // Two charge entries (one per issued invoice). REAL data, fully offline.
        Assert.Equal(2, data.GetArrayLength());
        Assert.All(
            Enumerable.Range(0, data.GetArrayLength()).Select(i => data[i]),
            e => Assert.Equal("Charge", e.GetProperty("kind").GetString()));
        // Chronological with running balance: first charge 1500, second cumulative 3000.
        Assert.Equal(1500m, data[0].GetProperty("amount").GetDecimal());
        Assert.Equal(1500m, data[0].GetProperty("runningBalance").GetDecimal());
        Assert.Equal(3000m, data[1].GetProperty("runningBalance").GetDecimal());
    }

    [Fact(DisplayName = "Lease history: includes payment entries (charge + payment chronology, running balance nets)")]
    public async Task History_IncludesPaymentEntries()
    {
        var accountId = await ActivateAsync("LEASE-0002", "CH-1", "PARTY-2");
        var subLedger = new SubLedgerAccountId(accountId);

        // One issued charge of 1500 on Jan 1.
        await SeedInvoiceAsync("INV-10", "CH-1", "PARTY-2", "INV-2026-01-01-AA-0010",
            new DateOnly(2026, 1, 1), 1500m, subLedger);
        // One inbound payment of 1500 applied to that invoice on Jan 15.
        await SeedPaymentAsync("PMT-10", "CH-1", "PARTY-2", "PE-0010",
            new DateOnly(2026, 1, 15), 1500m, targetInvoiceId: "INV-10");

        var doc = await _client.GetFromJsonAsync<JsonElement>($"{Base}/LEASE-0002/payment-history");
        var data = doc.GetProperty("data");
        Assert.Equal(2, data.GetArrayLength());

        // Charge first (Jan 1), payment second (Jan 15). Payment amount is negative; running nets to 0.
        Assert.Equal("Charge", data[0].GetProperty("kind").GetString());
        Assert.Equal(1500m, data[0].GetProperty("runningBalance").GetDecimal());
        Assert.Equal("Payment", data[1].GetProperty("kind").GetString());
        Assert.Equal(-1500m, data[1].GetProperty("amount").GetDecimal());
        Assert.Equal(0m, data[1].GetProperty("runningBalance").GetDecimal());
    }

    [Fact(DisplayName = "Lease history: FK-authoritative — a sibling lease's invoices do NOT leak (no PartyId-overmatch)")]
    public async Task History_FkAuthoritative_NoPartyOvermatch()
    {
        // Two leases for the SAME party + same AR control — the ADR 0120 overmatch case.
        var accountA = new SubLedgerAccountId(await ActivateAsync("LEASE-A", "CH-1", "PARTY-SHARED"));
        var accountB = new SubLedgerAccountId(await ActivateAsync("LEASE-B", "CH-1", "PARTY-SHARED"));
        Assert.NotEqual(accountA.Value, accountB.Value);

        await SeedInvoiceAsync("INV-A", "CH-1", "PARTY-SHARED", "INV-2026-01-01-AA-0100",
            new DateOnly(2026, 1, 1), 1000m, accountA);
        await SeedInvoiceAsync("INV-B", "CH-1", "PARTY-SHARED", "INV-2026-01-01-AA-0200",
            new DateOnly(2026, 1, 1), 2000m, accountB);

        var docA = await _client.GetFromJsonAsync<JsonElement>($"{Base}/LEASE-A/payment-history");
        var dataA = docA.GetProperty("data");
        Assert.Equal(1, dataA.GetArrayLength());
        Assert.Equal(1000m, dataA[0].GetProperty("amount").GetDecimal());

        var docB = await _client.GetFromJsonAsync<JsonElement>($"{Base}/LEASE-B/payment-history");
        var dataB = docB.GetProperty("data");
        Assert.Equal(1, dataB.GetArrayLength());
        Assert.Equal(2000m, dataB[0].GetProperty("amount").GetDecimal());
    }

    [Fact(DisplayName = "Lease history (C-T2-2): a payment applied to ONE lease's invoice appears in THAT lease's history ONLY (no same-party payment-leg over-emission)")]
    public async Task History_FkAuthoritative_PaymentNoPartyOvermatch()
    {
        // C-T2-2 (ADR-0122-P2 SPOT-CHECK binding condition): the dual of the charge overmatch test,
        // exercising the PAYMENT leg. Two leases for the SAME party + same AR control; a payment applied
        // to LEASE-A's invoice must NOT appear in (and net against) LEASE-B's history.
        var accountA = new SubLedgerAccountId(await ActivateAsync("LEASE-PA", "CH-1", "PARTY-OVER"));
        var accountB = new SubLedgerAccountId(await ActivateAsync("LEASE-PB", "CH-1", "PARTY-OVER"));
        Assert.NotEqual(accountA.Value, accountB.Value);

        // One issued charge per lease (both for the SAME party in the SAME chart).
        await SeedInvoiceAsync("INV-PA", "CH-1", "PARTY-OVER", "INV-2026-01-01-AA-0400",
            new DateOnly(2026, 1, 1), 1000m, accountA);
        await SeedInvoiceAsync("INV-PB", "CH-1", "PARTY-OVER", "INV-2026-01-01-AA-0500",
            new DateOnly(2026, 1, 1), 1000m, accountB);

        // A single inbound payment of 1000 applied ONLY to LEASE-A's invoice (INV-PA).
        await SeedPaymentAsync("PMT-PA", "CH-1", "PARTY-OVER", "PE-0400",
            new DateOnly(2026, 1, 15), 1000m, targetInvoiceId: "INV-PA");

        // LEASE-A: charge (1000) + payment (-1000) → 2 entries, running balance nets to 0.
        var docA = await _client.GetFromJsonAsync<JsonElement>($"{Base}/LEASE-PA/payment-history");
        var dataA = docA.GetProperty("data");
        Assert.Equal(2, dataA.GetArrayLength());
        Assert.Equal("Charge", dataA[0].GetProperty("kind").GetString());
        Assert.Equal("Payment", dataA[1].GetProperty("kind").GetString());
        Assert.Equal(-1000m, dataA[1].GetProperty("amount").GetDecimal());
        Assert.Equal(0m, dataA[1].GetProperty("runningBalance").GetDecimal());

        // LEASE-B: charge ONLY — the sibling lease's payment must NOT leak in. 1 entry, balance 1000.
        var docB = await _client.GetFromJsonAsync<JsonElement>($"{Base}/LEASE-PB/payment-history");
        var dataB = docB.GetProperty("data");
        Assert.Equal(1, dataB.GetArrayLength());
        Assert.Equal("Charge", dataB[0].GetProperty("kind").GetString());
        Assert.Equal(1000m, dataB[0].GetProperty("runningBalance").GetDecimal());
    }

    [Fact(DisplayName = "Lease history: a foreign-tenant invoice never leaks into the history")]
    public async Task History_NoForeignTenantLeak()
    {
        var accountId = new SubLedgerAccountId(await ActivateAsync("LEASE-T", "CH-1", "PARTY-1"));

        // A node-resident invoice for the local tenant, stamped with the lease account.
        await SeedInvoiceAsync("INV-LOCAL", "CH-1", "PARTY-1", "INV-2026-01-01-AA-0300",
            new DateOnly(2026, 1, 1), 500m, accountId);

        // A foreign-tenant invoice carrying the SAME sub-ledger account FK (structurally unreachable on
        // a real single-device node; seeded directly to prove the WHERE TenantId guard holds).
        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            var line = InvoiceLine.Create(
                invoiceId: new InvoiceId("INV-FOREIGN"), lineNumber: 1, description: "Rent",
                quantity: 1m, unitPrice: 999m, incomeAccountId: new GLAccountId("4000"));
            var foreign = Invoice.Create(
                tenantId: new TenantId("other-tenant"), chartId: new ChartOfAccountsId("CH-1"),
                invoiceNumber: "INV-2026-01-01-AA-0999", customerId: new PartyId("PARTY-1"),
                issueDate: new DateOnly(2026, 1, 1), dueDate: new DateOnly(2026, 1, 31),
                lines: new[] { line }, arAccountId: new GLAccountId("1100"),
                createdAtUtc: new Instant(System.TimeProvider.System.GetUtcNow()),
                id: new InvoiceId("INV-FOREIGN"), subLedgerAccountId: accountId) with
            {
                Status = InvoiceStatus.Issued,
            };
            ctx.Set<Invoice>().Add(foreign);
            await ctx.SaveChangesAsync();
        }

        var doc = await _client.GetFromJsonAsync<JsonElement>($"{Base}/LEASE-T/payment-history");
        var data = doc.GetProperty("data");
        Assert.Equal(1, data.GetArrayLength());
        Assert.Equal(500m, data[0].GetProperty("amount").GetDecimal());
    }
}
