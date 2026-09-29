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
using Harborline.Api.Blocks.FinancialAr.Services;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialLedger.Services;
using Harborline.Api.Blocks.FinancialPeriods.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.Foundation.Scheduling;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Audit;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Health;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// T-974: public HTTP tests for the recurring-invoice create and generate routes. The production
/// <see cref="RecurringInvoiceRoutes.Map"/> runs over the production-faithful node service composition the
/// crash-resume tests use (<see cref="NodeEfRecurringInvoiceService"/> over the node invoice repository,
/// numbering, posting and journal store).
/// </summary>
public sealed class RecurringInvoiceRouteTests : IAsyncLifetime
{
    private static readonly TenantId LocalTenantId =
        ActiveTeamTenantContext.ProjectTenantId(NodeTestActiveTeam.TestTeamId);
    private const string Route = RecurringInvoiceRoutes.RouteBase;

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private IDbContextFactory<LocalNodeDbContext> _factory = null!;
    private NodeEfInvoiceRepository _invoices = null!;
    private string _dir = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        // Ticket 294 slice 3b: generate attributes the act to the desktop operator.
        Harborline.Api.LocalNodeHost.Tests.Authorization.TestDesktopOperator.AddTestDesktopOperator(builder.Services);
        _dir = Path.Combine(Path.GetTempPath(), "harborline-recurring-routes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        builder.Services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialAr.Data.ArEntityModule>();
        builder.Services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialLedger.Data.FinancialLedgerEntityModule>();
        builder.Services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialPeriods.Data.FinancialPeriodsEntityModule>();
        builder.Services.AddSingleton<IHarborlineEntityModule, AuditEventEntityModule>();
        builder.Services.AddDbContextFactory<LocalNodeDbContext>(opt =>
            opt.UseSqlite($"Data Source={Path.Combine(_dir, "recurring-test.db")};Pooling=False"));
        _app = builder.Build();

        _factory = _app.Services.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
        await using (var ctx = await _factory.CreateDbContextAsync())
            await ctx.Database.EnsureCreatedAsync();
        await SeedAccountAsync("1100", "Accounts Receivable", GLAccountType.Asset, AccountSubtype.AccountsReceivable);
        await SeedAccountAsync("4000", "Service Income", GLAccountType.Revenue, AccountSubtype.OperatingIncome);
        await SeedOpenPeriodAsync(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));

        _invoices = new NodeEfInvoiceRepository(_factory);
        var journalStore = new NodeEfJournalStore(
            _factory,
            NodeJournalWriteAdapters.Create(recurringInvoice: new NodeRecurringInvoiceWriteEnlister()));
        var invoicePosting = new InvoicePostingService(
            tenantContext: new ActiveTeamTenantContext(NodeTestActiveTeam.Accessor),
            invoices:      _invoices,
            numbering:     new NodeEfInvoiceNumberingService(_factory, new ReplicaId("AA")),
            tax:           new NoOpTaxCalculator(),
            journals:      new JournalPostingService(
                accounts: new NodeEfAccountResolver(_factory),
                periods:  new NodeEfPeriodResolver(_factory),
                store:    journalStore,
                gate:     TestAuthorization.AllowGate()),
            events:        null,
            journalStore:  journalStore, timeProvider: TimeProvider.System);
        var recurring = new NodeEfRecurringInvoiceService(
            contextFactory: _factory,
            rrule:          new InMemoryRruleExpansionService(),
            posting:        invoicePosting,
            invoices:       _invoices);

        RecurringInvoiceRoutes.Map(_app, recurring, NodeTestActiveTeam.Accessor, TimeProvider.System);
        await _app.StartAsync();
        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
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

    [Theory(DisplayName = "Recurring create: a client-supplied schedule id is refused 400 with no write, and omission mints a server id (T-974)")]
    [InlineData("id")]
    [InlineData("scheduleId")]
    public async Task Create_refuses_a_client_supplied_record_id(string key)
    {
        var body = ScheduleBody();
        body[key] = "client-constructed-id";

        var refused = await _client.PostAsJsonAsync(Route, body);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("request.record-id-not-accepted",
            (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        var list = await _client.GetFromJsonAsync<JsonElement>(Route);
        Assert.Equal(0, list.GetProperty("schedules").GetArrayLength());

        var minted = await CreateScheduleAsync();
        Assert.False(string.IsNullOrWhiteSpace(minted));
        Assert.NotEqual("client-constructed-id", minted);
    }

    [Theory(DisplayName = "Recurring generate: a client-supplied invoice id is refused 400 with no invoice, and omission derives server ids (T-974)")]
    [InlineData("id")]
    [InlineData("invoiceId")]
    public async Task Generate_refuses_a_client_supplied_record_id(string key)
    {
        var scheduleId = await CreateScheduleAsync();

        var refused = await _client.PostAsJsonAsync($"{Route}/{scheduleId}/generate",
            new Dictionary<string, object?> { ["asOf"] = "2026-03-01", [key] = "client-constructed-id" });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("request.record-id-not-accepted",
            (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Empty(await _invoices.ListByChartAsync(LocalTenantId, new ChartOfAccountsId("CH-1")));

        var generated = await _client.PostAsJsonAsync($"{Route}/{scheduleId}/generate", new { asOf = "2026-03-01" });
        Assert.Equal(HttpStatusCode.OK, generated.StatusCode);
        var result = await generated.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Generated", result.GetProperty("outcome").GetString());
        var minted = Assert.Single(result.GetProperty("invoices").EnumerateArray()).GetProperty("invoiceId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(minted));
        Assert.NotEqual("client-constructed-id", minted);
    }

    private static Dictionary<string, object?> ScheduleBody() => new()
    {
        ["chartId"] = "CH-1",
        ["customerId"] = "customer-1",
        ["arAccountId"] = "1100",
        ["recurrenceRule"] = "FREQ=DAILY;INTERVAL=1",
        ["timezone"] = "UTC",
        ["startsOn"] = "2026-03-01",
        ["endsOn"] = "2026-03-01",
        ["lookaheadHorizonDays"] = 0,
        ["generateLeadDays"] = 0,
        ["lines"] = new[] { new { description = "Monthly service", quantity = 1m, unitPrice = 250m, incomeAccountId = "4000" } },
    };

    private async Task<string> CreateScheduleAsync()
    {
        var created = await _client.PostAsJsonAsync(Route, ScheduleBody());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("scheduleId").GetString()!;
    }

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
}
