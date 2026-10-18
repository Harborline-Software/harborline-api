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

    [Theory]
    [InlineData("FREQ=DAILY;INTERVAL=0", 2, "RecurrenceRule has an invalid INTERVAL value.")]
    [InlineData("FREQ=DAILY;EXDATE=20260106", 2, "RecurrenceRule contains an unsupported component.")]
    [InlineData("FREQ=DAILY", 1000, "RecurrenceRule exceeds the supported expansion limit for the requested range.")]
    public async Task Create_validates_recurrence_before_an_active_schedule_is_saved(string rule, int lookahead, string expectedDetail)
    {
        var body = ScheduleBody();
        body["recurrenceRule"] = rule;
        body["startsOn"] = "2026-01-01";
        body["endsOn"] = null;
        body["lookaheadHorizonDays"] = lookahead;

        var response = await _client.PostAsJsonAsync(Route, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var refusal = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("validation_failed", refusal.GetProperty("error").GetString());
        AssertSafeRecurrenceDetail(expectedDetail, refusal.GetProperty("detail").GetString()!);
        await using var context = await _factory.CreateDbContextAsync();
        Assert.Equal(0, await context.Set<RecurringInvoiceSchedule>().IgnoreQueryFilters().CountAsync());
        await AssertNoInvoiceOrJournalWritesAsync();
    }

    [Theory]
    [InlineData("FREQ=DAILY;INTERVAL=0", 2, "RecurrenceRule has an invalid INTERVAL value.")]
    [InlineData("FREQ=DAILY;EXDATE=20260106", 2, "RecurrenceRule contains an unsupported component.")]
    [InlineData("FREQ=DAILY", 1000, "RecurrenceRule exceeds the supported expansion limit for the requested range.")]
    public async Task Generate_returns_existing_failed_outcome_for_a_stored_recurrence_refusal(string rule, int lookahead, string expectedDetail)
    {
        var id = await SeedStoredRecurrenceAsync(rule, lookahead);

        var response = await _client.PostAsJsonAsync($"{Route}/{id}/generate", new { asOf = "2026-01-01" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Failed", result.GetProperty("outcome").GetString());
        Assert.Equal(0, result.GetProperty("invoices").GetArrayLength());
        Assert.Equal(0, result.GetProperty("newlyGeneratedCount").GetInt32());
        AssertSafeRecurrenceDetail(expectedDetail, result.GetProperty("error").GetString()!);
        await AssertNoInvoiceOrJournalWritesAsync();
        await using var context = await _factory.CreateDbContextAsync();
        var stored = await context.Set<RecurringInvoiceSchedule>().IgnoreQueryFilters().AsNoTracking().SingleAsync();
        Assert.Equal(rule, stored.RecurrenceRule);
        Assert.Equal(RecurringScheduleStatus.Active, stored.Status);
        Assert.Empty(stored.GeneratedInvoices);
        Assert.Null(stored.LastGeneratedAtUtc);
    }

    [Fact]
    public async Task A_complete_empty_expansion_is_valid_at_create_and_generate()
    {
        var body = ScheduleBody();
        body["recurrenceRule"] = "FREQ=MONTHLY;BYMONTHDAY=1";
        body["startsOn"] = "2026-01-02";
        body["endsOn"] = "2026-01-02";
        body["lookaheadHorizonDays"] = 0;

        var created = await _client.PostAsJsonAsync(Route, body);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var detail = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Active", detail.GetProperty("status").GetString());
        var id = detail.GetProperty("scheduleId").GetString();
        var generated = await _client.PostAsJsonAsync($"{Route}/{id}/generate", new { asOf = "2026-01-02" });
        Assert.Equal(HttpStatusCode.OK, generated.StatusCode);
        var result = await generated.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Generated", result.GetProperty("outcome").GetString());
        Assert.Equal(0, result.GetProperty("invoices").GetArrayLength());
        await AssertNoInvoiceOrJournalWritesAsync();
        await using var context = await _factory.CreateDbContextAsync();
        var stored = await context.Set<RecurringInvoiceSchedule>().IgnoreQueryFilters().AsNoTracking().SingleAsync();
        Assert.Equal("FREQ=MONTHLY;BYMONTHDAY=1", stored.RecurrenceRule);
        Assert.Equal(RecurringScheduleStatus.Active, stored.Status);
        Assert.Empty(stored.GeneratedInvoices);
        Assert.Null(stored.LastGeneratedAtUtc);
    }

    [Theory]
    [InlineData(false, "RecurrenceRule contains a malformed value.")]
    [InlineData(true, "RecurrenceRule contains an unsupported component.")]
    public async Task Recurrence_diagnostics_are_bounded_fixed_text_not_rule_or_stack(bool unsupported, string expectedDetail)
    {
        var untrusted = "DO-NOT-ECHO\r\n" + new string('x', 4096);
        var rule = unsupported ? "FREQ=DAILY;" + untrusted + "=1" : "FREQ=DAILY;INTERVAL=" + untrusted;
        var body = ScheduleBody();
        body["recurrenceRule"] = rule;
        var created = await _client.PostAsJsonAsync(Route, body);
        Assert.Equal(HttpStatusCode.BadRequest, created.StatusCode);
        var refusal = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("validation_failed", refusal.GetProperty("error").GetString());
        AssertSafeRecurrenceDetail(expectedDetail, refusal.GetProperty("detail").GetString()!);

        var id = await SeedStoredRecurrenceAsync(rule, 2);
        var generated = await _client.PostAsJsonAsync($"{Route}/{id}/generate", new { asOf = "2026-01-01" });
        Assert.Equal(HttpStatusCode.OK, generated.StatusCode);
        var result = await generated.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Failed", result.GetProperty("outcome").GetString());
        AssertSafeRecurrenceDetail(expectedDetail, result.GetProperty("error").GetString()!);
        Assert.Equal(0, result.GetProperty("invoices").GetArrayLength());
        await AssertNoInvoiceOrJournalWritesAsync();
    }

    [Theory]
    [InlineData("FREQ=DAILY;INTERVAL=0", "RecurrenceRule has an invalid INTERVAL value.")]
    [InlineData("FREQ=DAILY;COUNT=0", "RecurrenceRule has an invalid COUNT value.")]
    [InlineData("FREQ=DAILY;UNTIL=bad", "RecurrenceRule has an invalid UNTIL value.")]
    [InlineData("FREQ=MONTHLY;BYDAY=XX", "RecurrenceRule has an invalid BYDAY value.")]
    [InlineData("FREQ=MONTHLY;BYDAY=6MO", "RecurrenceRule has an invalid BYDAY value.")]
    [InlineData("FREQ=MONTHLY;BYMONTHDAY=29", "RecurrenceRule has an invalid BYMONTHDAY value.")]
    [InlineData("FREQ=YEARLY;BYMONTH=13", "RecurrenceRule has an invalid BYMONTH value.")]
    [InlineData("COUNT=2", "RecurrenceRule is missing its FREQ part.")]
    [InlineData("FREQ=HOURLY", "RecurrenceRule has an unsupported FREQ value.")]
    [InlineData("FREQ=WEEKLY;BYMONTHDAY=1", "RecurrenceRule BYMONTHDAY is not supported with WEEKLY.")]
    [InlineData("FREQ=DAILY;COUNT=INTERVAL", "RecurrenceRule has an invalid COUNT value.")]
    [InlineData("FREQ=DAILY;UNTIL=BYDAY", "RecurrenceRule has an invalid UNTIL value.")]
    [InlineData("FREQ=DAILY;BYDAY=COUNT", "RecurrenceRule has an invalid BYDAY value.")]
    [InlineData("FREQ=BYDAY", "RecurrenceRule has an unsupported FREQ value.")]
    [InlineData("FREQ=DAILY;X-COUNT=1", "RecurrenceRule contains an unsupported component.")]
    [InlineData("FREQ=DAILY;EXDATE=20260106", "RecurrenceRule contains an unsupported component.")]
    [InlineData("FREQ=DAILY;COUNT=DO-NOT-ECHO'INTERVAL", "RecurrenceRule contains a malformed value.")]
    [InlineData("FREQ=DAILY;COUNT=DO-NOT-ECHO\r\nINTERVAL", "RecurrenceRule contains a malformed value.")]
    public async Task Recurrence_part_details_use_pinned_templates_without_selecting_words_from_values(
        string rule, string expectedDetail)
    {
        var body = ScheduleBody();
        body["recurrenceRule"] = rule;
        body["startsOn"] = "2026-01-01";
        body["endsOn"] = null;
        body["lookaheadHorizonDays"] = 2;

        var created = await _client.PostAsJsonAsync(Route, body);
        Assert.Equal(HttpStatusCode.BadRequest, created.StatusCode);
        var refusal = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("validation_failed", refusal.GetProperty("error").GetString());
        AssertSafeRecurrenceDetail(expectedDetail, refusal.GetProperty("detail").GetString()!);
        await using (var afterCreate = await _factory.CreateDbContextAsync())
            Assert.Equal(0, await afterCreate.Set<RecurringInvoiceSchedule>().IgnoreQueryFilters().CountAsync());
        await AssertNoInvoiceOrJournalWritesAsync();

        var id = await SeedStoredRecurrenceAsync(rule, 2);
        var generated = await _client.PostAsJsonAsync($"{Route}/{id}/generate", new { asOf = "2026-01-01" });
        Assert.Equal(HttpStatusCode.OK, generated.StatusCode);
        var result = await generated.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Failed", result.GetProperty("outcome").GetString());
        Assert.Equal(0, result.GetProperty("invoices").GetArrayLength());
        Assert.Equal(0, result.GetProperty("newlyGeneratedCount").GetInt32());
        AssertSafeRecurrenceDetail(expectedDetail, result.GetProperty("error").GetString()!);
        await AssertNoInvoiceOrJournalWritesAsync();
        await using var storedRead = await _factory.CreateDbContextAsync();
        Assert.Equal(1, await storedRead.Set<RecurringInvoiceSchedule>().IgnoreQueryFilters().CountAsync());
        var stored = await storedRead.Set<RecurringInvoiceSchedule>().IgnoreQueryFilters().AsNoTracking().SingleAsync();
        Assert.Equal(rule, stored.RecurrenceRule);
        Assert.Equal(RecurringScheduleStatus.Active, stored.Status);
        Assert.Empty(stored.GeneratedInvoices);
        Assert.Null(stored.LastGeneratedAtUtc);
    }

    // A run of letters with no '=' leaves FREQ unset, so the pinned producer throws a plain FormatException
    // "RRULE missing FREQ= component: '<rule>'." (32 + rule length + 2 characters). The value has no quote or
    // control character, so it matches the missing-FREQ template, and only the 512-character message bound decides:
    // 478 letters is a 512-character message (mapped), 479 is 513 and 600 is 634 (both fixed generic text).
    [Theory]
    [InlineData(478, "RecurrenceRule is missing its FREQ part.")]
    [InlineData(479, "RecurrenceRule contains a malformed value.")]
    [InlineData(600, "RecurrenceRule contains a malformed value.")]
    public async Task An_oversized_producer_message_matching_a_template_returns_the_fixed_generic_detail(
        int ruleLength, string expectedDetail)
    {
        var rule = new string('x', ruleLength);
        var body = ScheduleBody();
        body["recurrenceRule"] = rule;
        body["startsOn"] = "2026-01-01";
        body["endsOn"] = null;
        body["lookaheadHorizonDays"] = 2;

        var created = await _client.PostAsJsonAsync(Route, body);
        Assert.Equal(HttpStatusCode.BadRequest, created.StatusCode);
        var refusal = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("validation_failed", refusal.GetProperty("error").GetString());
        AssertSafeRecurrenceDetail(expectedDetail, refusal.GetProperty("detail").GetString()!);
        await using (var afterCreate = await _factory.CreateDbContextAsync())
            Assert.Equal(0, await afterCreate.Set<RecurringInvoiceSchedule>().IgnoreQueryFilters().CountAsync());
        await AssertNoInvoiceOrJournalWritesAsync();

        var id = await SeedStoredRecurrenceAsync(rule, 2);
        var generated = await _client.PostAsJsonAsync($"{Route}/{id}/generate", new { asOf = "2026-01-01" });
        Assert.Equal(HttpStatusCode.OK, generated.StatusCode);
        var result = await generated.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Failed", result.GetProperty("outcome").GetString());
        Assert.Equal(0, result.GetProperty("invoices").GetArrayLength());
        AssertSafeRecurrenceDetail(expectedDetail, result.GetProperty("error").GetString()!);
        await AssertNoInvoiceOrJournalWritesAsync();
    }

    private static void AssertSafeRecurrenceDetail(string expected, string actual)
    {
        Assert.Equal(expected, actual);
        Assert.InRange(actual.Length, 1, 160);
        Assert.DoesNotContain("DO-NOT-ECHO", actual);
        Assert.DoesNotContain("Exception", actual);
        Assert.All(actual, character => Assert.False(char.IsControl(character)));
    }

    private async Task<string> SeedStoredRecurrenceAsync(string rule, int lookahead)
    {
        var id = new RecurringInvoiceScheduleId("00000000-0000-0000-0000-0000000000d1");
        var schedule = RecurringInvoiceSchedule.Create(
            LocalTenantId, new ChartOfAccountsId("CH-1"), new Harborline.Api.Blocks.People.Foundation.Models.PartyId("customer-1"),
            new GLAccountId("1100"), rule, new DateOnly(2026, 1, 1), "UTC",
            new[] { new RecurringInvoiceLineTemplate("Stored recurrence", 1m, 250m, new GLAccountId("4000")) },
            endsOn: null, lookaheadHorizonDays: lookahead, generateLeadDays: 0, id: id);
        await using var context = await _factory.CreateDbContextAsync();
        context.Set<RecurringInvoiceSchedule>().Add(schedule);
        await context.SaveChangesAsync();
        return id.Value;
    }

    private async Task AssertNoInvoiceOrJournalWritesAsync()
    {
        await using var context = await _factory.CreateDbContextAsync();
        Assert.Equal(0, await context.Set<Invoice>().IgnoreQueryFilters().CountAsync());
        Assert.Equal(0, await context.Set<JournalEntry>().IgnoreQueryFilters().CountAsync());
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
