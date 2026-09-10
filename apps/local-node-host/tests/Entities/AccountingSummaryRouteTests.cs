using System.Net;
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
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Blocks.FinancialAr.Data;
using Harborline.Api.Blocks.FinancialLedger.Data;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Health;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// T1 local-first sweep — route-level tests for the node-local accounting-summary surface
/// (dashboard GL aggregation + outstanding invoices) that replaces the Bridge ERPNext proxy.
/// </summary>
/// <remarks>
/// Seeds a chart via the seed route, then inserts posted journal entries (Revenue + Expense) and
/// an issued invoice directly via the EF context for determinism, and drives the routes the
/// <see cref="HostedAccountingSummaryApiEndpoint"/> registers over a real Kestrel listener.
/// </remarks>
public sealed class AccountingSummaryRouteTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private IDbContextFactory<LocalNodeDbContext> _factory = null!;
    private string _dir = null!;

    private static readonly TenantId LocalTenantId =
        Harborline.Api.LocalNodeHost.Data.Financial.ActiveTeamTenantContext.ProjectTenantId(NodeTestActiveTeam.TestTeamId);

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        _dir = Path.Combine(Path.GetTempPath(), "harborline-summary-routes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var connectionString = $"Data Source={Path.Combine(_dir, "summary-test.db")};Pooling=False";

        builder.Services.AddSingleton<IHarborlineEntityModule, FinancialLedgerEntityModule>();
        // Ticket 151: the entity POST is now permission-gated; grant-all keeps these route tests on their pre-gate behaviour.
        builder.Services.AddSingleton<Harborline.Api.Foundation.Authorization.IAuthorizationContext>(Harborline.Api.LocalNodeHost.Tests.Packs.TestAuthorizationContext.AllowAll());
        builder.Services.AddTestKernelClock();
        builder.Services.AddSingleton(Harborline.Api.LocalNodeHost.Tests.Authorization.TestRouteGate.AllowAll());
        builder.Services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialPeriods.Data.FinancialPeriodsEntityModule>();
        builder.Services.AddSingleton<IHarborlineEntityModule, ArEntityModule>();
        builder.Services.AddDbContextFactory<LocalNodeDbContext>(opt =>
            opt.UseSqlite(connectionString));

        _app = builder.Build();

        _factory = _app.Services.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            await ctx.Database.EnsureCreatedAsync();
        }

        _app.Use(async (http, next) =>
        {
            http.Features.Set(DesktopPlaneRequestFeature.Instance);
            await next(http);
        });
        var deviceReachable = _app.MapDeviceReachableProductDataGroup();
        EntityRoutes.Map(deviceReachable, _factory, NodeTestActiveTeam.Accessor,
            new Data.Entities.NodeEntityWriter(_factory, Harborline.Api.Foundation.Assets.Entities.TestEntityWritePipeline.Accepting, Data.Entities.TestNodeRecordSchemas.Fresh(), Authorization.TestAuthorization.AllowGate()),
            TimeProvider.System);
        ChartOfAccountsRoutes.Map(deviceReachable, _factory, NodeTestActiveTeam.Accessor, TimeProvider.System);
        AccountingSummaryRoutes.Map(
            deviceReachable,
            new NodeAccountingSummaryService(_factory, NodeTestActiveTeam.Accessor));

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

    private const string EntityRoute = "/api/local-node/entities";
    private const string SeedRoute = "/api/local-node/chart-of-accounts/seed-from-template";
    private const string SummaryRoute = "/api/local-node/accounting/summary";
    private const string OutstandingRoute = "/api/local-node/accounting/outstanding";

    private async Task<string> SeedChartAsync()
    {
        var entResp = await _client.PostAsJsonAsync(EntityRoute, new { legalName = "Summary Test LLC" });
        entResp.EnsureSuccessStatusCode();
        var entityId = (await entResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var seedResp = await _client.PostAsJsonAsync(SeedRoute, new { entityId, templateId = "rental-real-estate" });
        seedResp.EnsureSuccessStatusCode();
        return (await seedResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("chartId").GetString()!;
    }

    private async Task<GLAccountId> AccountIdByCodeAsync(ChartOfAccountsId chartId, string code)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        var acct = await ctx.Set<GLAccount>().AsNoTracking()
            .FirstAsync(a => a.ChartId == chartId && a.Code == code);
        return acct.Id;
    }

    [Fact(DisplayName = "Summary: pre-seed returns a zeroed summary (dashboard renders pre-data)")]
    public async Task Summary_PreSeed_Zeroed()
    {
        var resp = await _client.GetAsync(SummaryRoute);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0m, doc.GetProperty("income").GetDecimal());
        Assert.Equal(0m, doc.GetProperty("expenses").GetDecimal());
        Assert.Equal(0m, doc.GetProperty("net").GetDecimal());
    }

    [Fact(DisplayName = "Summary: posted Revenue + Expense entries this month aggregate to income/expenses/net")]
    public async Task Summary_AggregatesPostedEntries()
    {
        var chartIdStr = await SeedChartAsync();
        var chartId = new ChartOfAccountsId(chartIdStr);
        var revenue = await AccountIdByCodeAsync(chartId, "4100"); // Rental Income (Revenue)
        var cash = await AccountIdByCodeAsync(chartId, "1100");    // a Cash/Asset account
        var expense = await AccountIdByCodeAsync(chartId, "5100"); // Advertising (Expense)

        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            // Booking 1: revenue 1000 — Debit Cash 1000 / Credit Rental Income 1000.
            ctx.Set<JournalEntry>().Add(PostedEntry(chartId, today, "rev", [
                new JournalEntryLine(cash, 1000m, 0m),
                new JournalEntryLine(revenue, 0m, 1000m),
            ]));
            // Booking 2: expense 300 — Debit Advertising 300 / Credit Cash 300.
            ctx.Set<JournalEntry>().Add(PostedEntry(chartId, today, "exp", [
                new JournalEntryLine(expense, 300m, 0m),
                new JournalEntryLine(cash, 0m, 300m),
            ]));
            await ctx.SaveChangesAsync();
        }

        var doc = await (await _client.GetAsync(SummaryRoute)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1000m, doc.GetProperty("income").GetDecimal());
        Assert.Equal(300m, doc.GetProperty("expenses").GetDecimal());
        Assert.Equal(700m, doc.GetProperty("net").GetDecimal());
        Assert.Equal($"{today:yyyy-MM}", doc.GetProperty("period").GetString());
    }

    [Fact(DisplayName = "Summary: Draft entries are excluded (posted-only discipline)")]
    public async Task Summary_ExcludesDrafts()
    {
        var chartIdStr = await SeedChartAsync();
        var chartId = new ChartOfAccountsId(chartIdStr);
        var revenue = await AccountIdByCodeAsync(chartId, "4100");
        var cash = await AccountIdByCodeAsync(chartId, "1100");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            // A DRAFT revenue entry — must NOT contribute.
            var draft = PostedEntry(chartId, today, "draft", [
                new JournalEntryLine(cash, 500m, 0m),
                new JournalEntryLine(revenue, 0m, 500m),
            ]) with { Status = JournalEntryStatus.Draft };
            ctx.Set<JournalEntry>().Add(draft);
            await ctx.SaveChangesAsync();
        }

        var doc = await (await _client.GetAsync(SummaryRoute)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0m, doc.GetProperty("income").GetDecimal());
    }

    [Fact(DisplayName = "Outstanding: pre-seed returns an empty list")]
    public async Task Outstanding_PreSeed_Empty()
    {
        var doc = await (await _client.GetAsync(OutstandingRoute)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(doc.GetProperty("data").EnumerateArray());
    }

    [Fact(DisplayName = "Outstanding: an Issued invoice with a positive balance is listed; a Paid one is not")]
    public async Task Outstanding_ListsOpenInvoices()
    {
        var chartIdStr = await SeedChartAsync();
        var chartId = new ChartOfAccountsId(chartIdStr);
        var ar = await AccountIdByCodeAsync(chartId, "1130"); // an AR account in the template
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            var openInv = IssuedInvoice(chartId, "INV-OPEN", ar, today, balance: 250m, status: InvoiceStatus.Issued);
            var paidInv = IssuedInvoice(chartId, "INV-PAID", ar, today, balance: 0m, status: InvoiceStatus.Paid);
            ctx.Set<Invoice>().AddRange(openInv, paidInv);
            await ctx.SaveChangesAsync();
        }

        var doc = await (await _client.GetAsync(OutstandingRoute)).Content.ReadFromJsonAsync<JsonElement>();
        var rows = doc.GetProperty("data").EnumerateArray().ToList();
        Assert.Single(rows);
        Assert.Equal("INV-OPEN", rows[0].GetProperty("name").GetString());
        Assert.Equal(250m, rows[0].GetProperty("outstandingAmount").GetDecimal());
    }

    // ── builders ──────────────────────────────────────────────────────────────────

    private static JournalEntry PostedEntry(
        ChartOfAccountsId chartId, DateOnly date, string memo, IReadOnlyList<JournalEntryLine> lines)
        => new(
            id:           JournalEntryId.NewId(),
            tenantId:     LocalTenantId,
            entryDate:    date,
            memo:         memo,
            lines:        lines,
            createdAtUtc: new Instant(System.TimeProvider.System.GetUtcNow()))
        {
            ChartId = chartId,
            Status = JournalEntryStatus.Posted,
            SourceKind = JournalEntrySource.Manual,
        };

    private static Invoice IssuedInvoice(
        ChartOfAccountsId chartId, string number, GLAccountId arAccount, DateOnly date,
        decimal balance, InvoiceStatus status)
    {
        var invId = InvoiceId.NewId();
        var line = InvoiceLine.Create(
            invoiceId: invId,
            lineNumber: 1,
            description: "Test",
            quantity: 1m,
            unitPrice: balance == 0m ? 100m : balance,
            incomeAccountId: arAccount);
        var inv = Invoice.Create(
            id:            invId,
            tenantId:      LocalTenantId,
            chartId:       chartId,
            invoiceNumber: number,
            customerId:    new PartyId("cust-1"),
            issueDate:     date,
            dueDate:       date.AddDays(30),
            lines:         [line],
            arAccountId:   arAccount,
            createdAtUtc:  new Instant(System.TimeProvider.System.GetUtcNow()));
        return inv with { Status = status, Balance = balance, AmountPaid = inv.Total - balance };
    }
}
