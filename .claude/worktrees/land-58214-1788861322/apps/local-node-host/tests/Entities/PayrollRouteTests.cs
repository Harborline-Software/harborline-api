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

using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialLedger.Services;
using Harborline.Api.Blocks.Payroll.Services;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Payroll;
using Harborline.Api.LocalNodeHost.Health;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// End-to-end route tests for the node payroll surface (T4 local-first sweep — the payroll node-flip;
/// ADR 0113 ABSOLUTE local-first). Spins up the REAL <see cref="PayrollRoutes"/> on an in-process
/// Kestrel listener backed by a temp SQLite store (a node-exclusive
/// <see cref="NodeLocalPayrollDbContext"/> + the shared <see cref="LocalNodeDbContext"/> for the GL),
/// driven by a real <see cref="HttpClient"/>. The pay-run post path posts a balanced double-entry
/// journal entry through the SAME node posting composition (<c>NodeEfJournalStore</c>) the JE/bill/
/// invoice paths use — the offline-acceptance proof for "post creates node JEs" (signal-bridge STOPPED).
/// </summary>
public sealed class PayrollRouteTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private string _dir = null!;
    private IDbContextFactory<LocalNodeDbContext> _financialFactory = null!;
    private IDbContextFactory<NodeLocalPayrollDbContext> _payrollFactory = null!;
    private NodeEfJournalStore _journalStore = null!;

    private static readonly TenantId LocalTenantId =
        Harborline.Api.LocalNodeHost.Data.Financial.ActiveTeamTenantContext.ProjectTenantId(NodeTestActiveTeam.TestTeamId);
    private const string EmployeesRoute = "/api/local-node/payroll/employees";
    private const string PayRunsRoute = "/api/local-node/payroll/pay-runs";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        _dir = Path.Combine(Path.GetTempPath(), "harborline-payroll-routes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var connectionString = $"Data Source={Path.Combine(_dir, "payroll-test.db")};Pooling=False";

        // The GL (JournalEntry + GLAccount) is contributed by FinancialLedgerEntityModule into the
        // shared LocalNodeDbContext; payroll persists into its OWN node-exclusive context on a separate
        // file (the two-context pattern). Both keyless here (the SqlCipher interceptor is a prod concern).
        builder.Services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialLedger.Data.FinancialLedgerEntityModule>();
        builder.Services.AddSingleton<IHarborlineEntityModule, Harborline.Api.LocalNodeHost.Data.Audit.AuditEventEntityModule>();
        builder.Services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialPeriods.Data.FinancialPeriodsEntityModule>();
        builder.Services.AddDbContextFactory<LocalNodeDbContext>(opt => opt.UseSqlite(connectionString));
        var payrollConnectionString = $"Data Source={Path.Combine(_dir, "payroll-store.db")};Pooling=False";
        builder.Services.AddDbContextFactory<NodeLocalPayrollDbContext>(opt =>
            opt.UseSqlite(payrollConnectionString,
                sqlite => sqlite.MigrationsHistoryTable(NodeLocalPayrollDbContext.MigrationsHistoryTableName)));

        _app = builder.Build();

        _financialFactory = _app.Services.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
        _payrollFactory = _app.Services.GetRequiredService<IDbContextFactory<NodeLocalPayrollDbContext>>();
        await using (var ctx = await _financialFactory.CreateDbContextAsync())
            await ctx.Database.EnsureCreatedAsync();
        await using (var pctx = await _payrollFactory.CreateDbContextAsync())
            await pctx.Database.EnsureCreatedAsync();

        // Production-faithful composition: the node payroll repos + the node posting service over the
        // node resolvers + the node journal store. PostAsync posts the pay-run's balanced JE through
        // this SAME JournalPostingService into the SAME NodeEfJournalStore.
        _journalStore = new NodeEfJournalStore(_financialFactory, NodeJournalWriteAdapters.Create());
        var postingService = new JournalPostingService(
            accounts: new NodeEfAccountResolver(_financialFactory),
            periods:  new NodeEfPeriodResolver(_financialFactory),
            store:    _journalStore,
            gate:     TestAuthorization.AllowGate());

        var employeeRepo = new NodeEfEmployeeRepository(_payrollFactory);
        var payRunRepo = new NodeEfPayRunRepository(_payrollFactory);
        var payrollPostingService = new PayRunPostingService(
            tenantContext: new Harborline.Api.LocalNodeHost.Data.Financial.ActiveTeamTenantContext(NodeTestActiveTeam.Accessor),
            payRuns:       payRunRepo,
            employees:     employeeRepo,
            journals:      postingService, timeProvider: TimeProvider.System);

        var payroll = (
            EmployeeRepo: employeeRepo,
            PayRunRepo: payRunRepo,
            PostingService: payrollPostingService);

        // Seed the GL accounts the pay-run JE posts to (UNSCOPED — no chart — so period-gating is skipped;
        // only account validity applies). Wage expense (6200), wages payable (2100), tax withheld (2200),
        // deductions payable (2300), employer-liability expense (6300) + payable (2400).
        await SeedAccountAsync("6200", "Wages Expense", GLAccountType.Expense, AccountSubtype.OperatingExpense);
        await SeedAccountAsync("2100", "Wages Payable", GLAccountType.Liability, AccountSubtype.AccountsPayable);
        await SeedAccountAsync("2200", "PAYE Withholding", GLAccountType.Liability, AccountSubtype.AccountsPayable);
        await SeedAccountAsync("2300", "Deductions Payable", GLAccountType.Liability, AccountSubtype.AccountsPayable);
        await SeedAccountAsync("6300", "Employer Tax Expense", GLAccountType.Expense, AccountSubtype.OperatingExpense);
        await SeedAccountAsync("2400", "Employer Tax Payable", GLAccountType.Liability, AccountSubtype.AccountsPayable);

        _app.Use(async (http, next) =>
        {
            http.Features.Set(DesktopPlaneRequestFeature.Instance);
            await next(http);
        });
        PayrollRoutes.Map(
            _app.MapDeviceReachableProductDataGroup(),
            payroll,
            NodeTestActiveTeam.Accessor, timeProvider: TimeProvider.System);

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

    // ── Employees ─────────────────────────────────────────────────────────────

    [Fact(DisplayName = "POST employee → GET employees round-trips node-side (offline)")]
    public async Task CreateAndListEmployee_RoundTrips()
    {
        var create = await _client.PostAsJsonAsync(EmployeesRoute, new
        {
            partyId = "party-1",
            displayName = "Ada Lovelace",
            wageExpenseAccountId = "6200",
            wagesPayableAccountId = "2100",
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var list = await _client.GetFromJsonAsync<JsonElement>(EmployeesRoute, JsonOpts);
        var employees = list.GetProperty("employees");
        Assert.Equal(1, employees.GetArrayLength());
        Assert.Equal("Ada Lovelace", employees[0].GetProperty("displayName").GetString());
        Assert.Equal("party-1", employees[0].GetProperty("partyId").GetString());
        Assert.True(employees[0].GetProperty("isActive").GetBoolean());
    }

    // ── Pay runs ──────────────────────────────────────────────────────────────

    [Fact(DisplayName = "POST pay-run with lines → GET detail round-trips lines node-side (offline)")]
    public async Task CreatePayRun_RoundTripsLines()
    {
        var empId = await CreateEmployeeAsync();
        var created = await CreatePayRunAsync(empId);
        var payRunId = created.GetProperty("payRunId").GetString()!;

        var detail = await _client.GetFromJsonAsync<JsonElement>($"{PayRunsRoute}/{payRunId}", JsonOpts);
        Assert.Equal("Draft", detail.GetProperty("status").GetString());
        Assert.Equal(1, detail.GetProperty("lines").GetArrayLength());
        Assert.Equal(empId, detail.GetProperty("lines")[0].GetProperty("employeeId").GetString());
        // NetWage = gross(1000) - tax(150) - deductions(50) = 800
        Assert.Equal(800m, detail.GetProperty("lines")[0].GetProperty("netWage").GetDecimal());
    }

    [Fact(DisplayName = "POST pay-run/{id}/post → status Posted + a BALANCED node JE is created (offline GL post)")]
    public async Task PostPayRun_CreatesBalancedNodeJournalEntry()
    {
        var empId = await CreateEmployeeAsync();
        var created = await CreatePayRunAsync(empId);
        var payRunId = created.GetProperty("payRunId").GetString()!;

        var post = await _client.PostAsync($"{PayRunsRoute}/{payRunId}/post", content: null);
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        var posted = await post.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        Assert.Equal("Posted", posted.GetProperty("status").GetString());
        var jeId = posted.GetProperty("journalEntryId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(jeId));

        // The JE landed in the recoverable node journal store and is balanced (Dr == Cr).
        var je = await FindJournalEntryAsync(jeId!);
        Assert.NotNull(je);
        var totalDebit = je!.Lines.Sum(l => l.Debit);
        var totalCredit = je.Lines.Sum(l => l.Credit);
        Assert.Equal(totalDebit, totalCredit);
        // Total debit = gross(1000) + employer-liability(100) = 1100.
        Assert.Equal(1100m, totalDebit);
    }

    [Fact(DisplayName = "POST pay-run/{id}/reverse → status Reversed + a reversal JE is posted (reverse-not-delete)")]
    public async Task ReversePayRun_PostsReversalEntry()
    {
        var empId = await CreateEmployeeAsync();
        var created = await CreatePayRunAsync(empId);
        var payRunId = created.GetProperty("payRunId").GetString()!;

        await _client.PostAsync($"{PayRunsRoute}/{payRunId}/post", content: null);

        var reverse = await _client.PostAsJsonAsync($"{PayRunsRoute}/{payRunId}/reverse", new { reason = "correction" });
        Assert.Equal(HttpStatusCode.OK, reverse.StatusCode);
        var reversed = await reverse.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        Assert.Equal("Reversed", reversed.GetProperty("status").GetString());
        Assert.False(string.IsNullOrWhiteSpace(reversed.GetProperty("reversalEntryId").GetString()));

        // Both the original + the reversal remain in the GL (reverse-not-delete).
        await using var ctx = await _financialFactory.CreateDbContextAsync();
        var count = await ctx.Set<JournalEntry>().CountAsync(e => e.TenantId == LocalTenantId);
        Assert.Equal(2, count);
    }

    [Fact(DisplayName = "GET pay-run/{unknown} → 404 (opaque)")]
    public async Task GetUnknownPayRun_Returns404()
    {
        var resp = await _client.GetAsync($"{PayRunsRoute}/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact(DisplayName = "GET pay-runs lists most-recent-first node-side (offline)")]
    public async Task ListPayRuns_ReturnsNodeData()
    {
        var empId = await CreateEmployeeAsync();
        await CreatePayRunAsync(empId);

        var list = await _client.GetFromJsonAsync<JsonElement>(PayRunsRoute, JsonOpts);
        Assert.Equal(1, list.GetProperty("payRuns").GetArrayLength());
        Assert.Equal(1, list.GetProperty("payRuns")[0].GetProperty("lineCount").GetInt32());
    }

    // ── Fixtures ────────────────────────────────────────────────────────────────

    private async Task<string> CreateEmployeeAsync()
    {
        var resp = await _client.PostAsJsonAsync(EmployeesRoute, new
        {
            partyId = "party-1",
            displayName = "Ada Lovelace",
            wageExpenseAccountId = "6200",
            wagesPayableAccountId = "2100",
        });
        resp.EnsureSuccessStatusCode();
        var emp = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        return emp.GetProperty("employeeId").GetString()!;
    }

    private async Task<JsonElement> CreatePayRunAsync(string employeeId)
    {
        var resp = await _client.PostAsJsonAsync(PayRunsRoute, new
        {
            label = "June 2026 bi-weekly",
            periodStart = "2026-06-01",
            periodEnd = "2026-06-15",
            postingDate = "2026-06-15",
            defaultTaxWithheldAccountId = "2200",
            defaultDeductionPayableAccountId = "2300",
            defaultEmployerLiabilityExpenseAccountId = "6300",
            defaultEmployerLiabilityPayableAccountId = "2400",
            lines = new[]
            {
                new
                {
                    employeeId,
                    grossWage = 1000m,
                    taxWithheld = 150m,
                    employeeDeductions = 50m,
                    employerLiabilityAmount = 100m,
                },
            },
        });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
    }

    private async Task<JournalEntry?> FindJournalEntryAsync(string jeId)
    {
        await using var ctx = await _financialFactory.CreateDbContextAsync();
        // JournalEntry.Lines is a JSONB-converted column on LocalNodeDbContext — a single SELECT
        // returns the fully-hydrated entry (no Include needed; mirrors NodeEfJournalStore reads).
        return await ctx.Set<JournalEntry>()
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.TenantId == LocalTenantId && e.Id == new JournalEntryId(jeId));
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

        await using var ctx = await _financialFactory.CreateDbContextAsync();
        ctx.Set<GLAccount>().Add(account);
        await ctx.SaveChangesAsync();
    }
}
