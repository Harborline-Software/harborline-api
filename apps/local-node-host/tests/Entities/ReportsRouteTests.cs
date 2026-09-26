using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Security.Cryptography;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialLedger.Services;
using Harborline.Api.Blocks.Reports;
using Harborline.Api.Blocks.Reports.DependencyInjection;
using Harborline.Api.Blocks.FinancialAp.Data;
using Harborline.Api.Blocks.FinancialAr.Data;
using Harborline.Api.Blocks.FinancialLedger.Data;
using Harborline.Api.Blocks.People.Foundation.Data;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.People;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Enrollment;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Authorization;
using Harborline.Api.Kernel.Audit;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// T5 local-first sweep — route-level tests for the node-local reports surface (the read-side report
/// cartridge family + chart list) that replaces the Bridge <c>/api/v1/reports</c> + <c>/api/v1/charts</c>.
/// </summary>
/// <remarks>
/// <para>
/// Wires the SAME reports read composition <c>Program.cs</c> wires (the two new node read seams + the
/// cartridge substrate over the node read deps) over a real Kestrel listener, seeds a chart + a posted
/// balanced JE directly via the EF context, and drives the routes
/// <see cref="HostedReportsApiEndpoint"/>/<see cref="ReportsRoutes"/> register. PROVES all six report
/// kinds COMPUTE node-side (offline over the node GL — signal-bridge not involved) + the chart list +
/// the single-device chart guard.
/// </para>
/// <para>
/// The Trial Balance / Balance Sheet asserts also confirm the node GL read-model is wired correctly
/// (<see cref="InMemoryGeneralLedgerReadModel"/> over <see cref="NodeEfJournalStore"/>): a posted
/// balanced entry nets the trial balance to zero.
/// </para>
/// </remarks>
public sealed class ReportsRouteTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private IDbContextFactory<LocalNodeDbContext> _factory = null!;
    private string _dir = null!;
    private readonly List<AuthorizationGateRequest> _decisions = [];

    private static readonly TenantId LocalTenantId =
        Harborline.Api.LocalNodeHost.Data.Financial.ActiveTeamTenantContext.ProjectTenantId(NodeTestActiveTeam.TestTeamId);

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddTestKernelClock();
        builder.Services.AddSingleton(TestAuthorization.Gate(true, _decisions.Add));

        _dir = Path.Combine(Path.GetTempPath(), "harborline-reports-routes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var connectionString = $"Data Source={Path.Combine(_dir, "reports-test.db")};Pooling=False";

        // Entity modules the reports read path touches (GL + AR + AP + People for the aging cartridges).
        builder.Services.AddSingleton<IHarborlineEntityModule, FinancialLedgerEntityModule>();
        builder.Services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialPeriods.Data.FinancialPeriodsEntityModule>();
        builder.Services.AddSingleton<IHarborlineEntityModule, ArEntityModule>();
        builder.Services.AddSingleton<IHarborlineEntityModule, ApEntityModule>();
        builder.Services.AddSingleton<IHarborlineEntityModule, PeopleEntityModule>();
        builder.Services.AddDbContextFactory<LocalNodeDbContext>(opt => opt.UseSqlite(connectionString));

        // The reports read composition — the SAME registrations Program.cs makes.
        builder.Services.AddSingleton<NodeEfJournalStore>();
        builder.Services.AddSingleton<IJournalStore>(sp => sp.GetRequiredService<NodeEfJournalStore>());
        builder.Services.AddSingleton<Harborline.Api.Blocks.FinancialPeriods.Services.IChartRepository, NodeEfChartRepository>();
        builder.Services.AddSingleton<IGeneralLedgerReadModel>(sp =>
            new InMemoryGeneralLedgerReadModel(sp.GetRequiredService<IJournalStore>()));
        // IAccountResolver — the node EF resolver over local-node.db (the cartridges enumerate accounts).
        builder.Services.AddSingleton<IAccountResolver, NodeEfAccountResolver>();
        // IFiscalPeriodRepository — the node EF repo (Trial Balance / Balance Sheet read the chart's periods).
        builder.Services.AddSingleton<Harborline.Api.Blocks.FinancialPeriods.Services.IFiscalPeriodRepository,
            Harborline.Api.LocalNodeHost.Data.Banking.NodeEfFiscalPeriodRepository>();
        // The ambient single-device tenant context (the aging services + invoice/bill repos read it).
        builder.Services.AddSingleton<Harborline.Api.Foundation.MultiTenancy.ITenantContext, StaticNodeTenantContext>();
        // AR/AP repos (the aging services read them) — node EF over local-node.db.
        builder.Services.AddSingleton<NodeEfInvoiceRepository>();
        builder.Services.AddSingleton<Harborline.Api.Blocks.FinancialAr.Services.IInvoiceRepository>(
            sp => sp.GetRequiredService<NodeEfInvoiceRepository>());
        builder.Services.AddSingleton<NodeEfBillRepository>();
        builder.Services.AddSingleton<Harborline.Api.Blocks.FinancialAp.Services.IBillRepository>(
            sp => sp.GetRequiredService<NodeEfBillRepository>());
        // IPartyReadModel (the AR/AP aging cartridges resolve party names) — node EF over local-node.db.
        builder.Services.AddSingleton<NodeEfPartyRepository>();
        builder.Services.AddSingleton<Harborline.Api.Blocks.People.Foundation.Services.IPartyReadModel>(
            sp => sp.GetRequiredService<NodeEfPartyRepository>());
        // AR/AP aging services (narrowed MultiTenancy.ITenantContext consumer variant).
        builder.Services.AddSingleton<Harborline.Api.Blocks.FinancialAr.Services.IArAgingService>(sp =>
            new Harborline.Api.Blocks.FinancialAr.Services.ArAgingService(
                sp.GetRequiredService<Harborline.Api.Foundation.MultiTenancy.ITenantContext>(),
                sp.GetRequiredService<Harborline.Api.Blocks.FinancialAr.Services.IInvoiceRepository>()));
        builder.Services.AddSingleton<Harborline.Api.Blocks.FinancialAp.Services.IApAgingService>(sp =>
            new Harborline.Api.Blocks.FinancialAp.Services.ApAgingService(
                sp.GetRequiredService<Harborline.Api.Foundation.MultiTenancy.ITenantContext>(),
                sp.GetRequiredService<Harborline.Api.Blocks.FinancialAp.Services.IBillRepository>()));
        // The report cartridge substrate + the six cartridges the pages use.
        builder.Services.AddBlocksReportsSubstrate();
        builder.Services.AddTrialBalanceCartridge();
        builder.Services.AddArAgingSummaryCartridge();
        builder.Services.AddApAgingSummaryCartridge();
        builder.Services.AddBalanceSheetCartridge();
        builder.Services.AddProfitAndLossCartridge();
        builder.Services.AddProfitAndLossByPropertyCartridge();

        _app = builder.Build();
        _factory = _app.Services.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            await ctx.Database.EnsureCreatedAsync();
        }

        // Drain the cartridge registrars (the HostedReportsApiEndpoint does this in StartAsync; the test
        // wires the routes directly, so drain here against the built provider).
        _app.Services.UseBlocksReports();

        // Map the chart-of-accounts MANAGEMENT routes (to create accounts) + the reports routes under test.
        _app.Use(async (http, next) =>
        {
            http.Features.Set(DesktopPlaneRequestFeature.Instance);
            await next(http);
        });
        var deviceReachable = _app.MapDeviceReachableProductDataGroup();
        ChartOfAccountsManagementRoutes.Map(deviceReachable, _factory, TimeProvider.System);
        var runner = _app.Services.GetRequiredService<IReportRunner>();
        ReportsRoutes.Map(deviceReachable, runner, _factory, NodeTestActiveTeam.Accessor);

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

    private const string ChartsRoute = "/api/local-node/charts";
    private const string ReportsBase = "/api/local-node/reports";

    // ── chart list ──────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "Charts: pre-seed returns an empty list (page renders pre-data)")]
    public async Task Charts_PreSeed_Empty()
    {
        var resp = await _client.GetAsync(ChartsRoute);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(doc.GetProperty("charts").EnumerateArray());
    }

    [Fact(DisplayName = "Charts: the install chart is listed as { chartId, name, baseCurrency }")]
    public async Task Charts_ListsInstallChart()
    {
        var chartId = await SeedChartWithAccountsAsync();

        var doc = await (await _client.GetAsync(ChartsRoute)).Content.ReadFromJsonAsync<JsonElement>();
        var charts = doc.GetProperty("charts").EnumerateArray().ToList();
        Assert.Single(charts);
        Assert.Equal(chartId.Value, charts[0].GetProperty("chartId").GetString());
        Assert.Equal("USD", charts[0].GetProperty("baseCurrency").GetString());
        Assert.False(string.IsNullOrWhiteSpace(charts[0].GetProperty("name").GetString()));
    }

    // ── report run — each kind computes node-side ─────────────────────────────────────

    [Fact(DisplayName = "Trial Balance: computes node-side over the node GL — a posted balanced entry nets to balanced")]
    public async Task TrialBalance_ComputesNodeSide()
    {
        var chartId = await SeedChartWithAccountsAsync();
        await PostBalancedEntryAsync(chartId);

        var resp = await _client.PostAsJsonAsync($"{ReportsBase}/trial-balance",
            new { chartId = chartId.Value, asOfDate = $"{DateOnly.FromDateTime(DateTime.UtcNow):yyyy-MM-dd}" });
        AssertReportAllowed(resp, ReportKind.TrialBalance, chartId);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var result = doc.GetProperty("result");
        Assert.Equal(chartId.Value, result.GetProperty("chartId").GetString());
        // A single posted balanced entry → total debit == total credit → isBalanced.
        Assert.True(result.GetProperty("isBalanced").GetBoolean());
        Assert.Equal(result.GetProperty("totalDebit").GetDecimal(), result.GetProperty("totalCredit").GetDecimal());
    }

    [Fact(DisplayName = "Balance Sheet: computes node-side over the node GL")]
    public async Task BalanceSheet_ComputesNodeSide()
    {
        var chartId = await SeedChartWithAccountsAsync();
        await PostBalancedEntryAsync(chartId);

        var resp = await _client.PostAsJsonAsync($"{ReportsBase}/balance-sheet",
            new { chartId = chartId.Value, asOfDate = $"{DateOnly.FromDateTime(DateTime.UtcNow):yyyy-MM-dd}" });
        AssertReportAllowed(resp, ReportKind.BalanceSheet, chartId);
        var result = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result");
        Assert.Equal(chartId.Value, result.GetProperty("chartId").GetString());
    }

    [Fact(DisplayName = "Profit and Loss: computes node-side over the node GL")]
    public async Task ProfitAndLoss_ComputesNodeSide()
    {
        var chartId = await SeedChartWithAccountsAsync();
        await PostBalancedEntryAsync(chartId);

        var resp = await _client.PostAsJsonAsync($"{ReportsBase}/profit-and-loss",
            new { chartId = chartId.Value });
        AssertReportAllowed(resp, ReportKind.ProfitAndLoss, chartId);
        var result = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result");
        Assert.Equal(chartId.Value, result.GetProperty("chartId").GetString());
    }

    [Fact(DisplayName = "Profit and Loss by Property: computes node-side over the node GL")]
    public async Task ProfitAndLossByProperty_ComputesNodeSide()
    {
        var chartId = await SeedChartWithAccountsAsync();
        await PostBalancedEntryAsync(chartId);

        var resp = await _client.PostAsJsonAsync($"{ReportsBase}/profit-and-loss-by-property",
            new { chartId = chartId.Value });
        AssertReportAllowed(resp, ReportKind.ProfitAndLossByProperty, chartId);
        var result = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result");
        Assert.Equal(chartId.Value, result.GetProperty("chartId").GetString());
    }

    [Fact(DisplayName = "AR Aging Summary: computes node-side over the node AR store")]
    public async Task ArAgingSummary_ComputesNodeSide()
    {
        var chartId = await SeedChartWithAccountsAsync();

        var resp = await _client.PostAsJsonAsync($"{ReportsBase}/ar-aging-summary",
            new { chartId = chartId.Value });
        AssertReportAllowed(resp, ReportKind.ArAgingSummary, chartId);
        var result = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result");
        Assert.Equal(chartId.Value, result.GetProperty("chartId").GetString());
        // No invoices seeded — totals present + zeroed (the report still computes offline).
        Assert.True(result.TryGetProperty("totals", out _));
    }

    [Fact(DisplayName = "AP Aging Summary: computes node-side over the node AP store")]
    public async Task ApAgingSummary_ComputesNodeSide()
    {
        var chartId = await SeedChartWithAccountsAsync();

        var resp = await _client.PostAsJsonAsync($"{ReportsBase}/ap-aging-summary",
            new { chartId = chartId.Value });
        AssertReportAllowed(resp, ReportKind.ApAgingSummary, chartId);
        var result = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result");
        Assert.Equal(chartId.Value, result.GetProperty("chartId").GetString());
        Assert.True(result.TryGetProperty("totals", out _));
    }

    private void AssertReportAllowed(HttpResponseMessage response, ReportKind kind, ChartOfAccountsId chartId)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var decision = Assert.Single(_decisions);
        Assert.Equal("reports:run", decision.Act.Operation.Value);
        Assert.Equal(LocalTenantId, decision.Tenant);
        Assert.Equal("reports", decision.Target.RecordKind);
        var recordId = $"{kind.ToKebab()}:{Convert.ToHexString(SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(chartId.Value)))}";
        Assert.Equal(recordId, decision.Target.RecordId);
        Assert.Equal($"/records/{recordId}", decision.Target.Scope.ToString());
    }

    // ── chart guard ───────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "Chart guard: a ChartId that is not the install chart → 404 (single-device guard)")]
    public async Task ReportRun_UnknownChart_NotFound()
    {
        await SeedChartWithAccountsAsync();

        var resp = await _client.PostAsJsonAsync($"{ReportsBase}/trial-balance",
            new { chartId = "not-the-install-chart" });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact(DisplayName = "Chart guard: report run pre-seed (no chart) → 404")]
    public async Task ReportRun_PreSeed_NotFound()
    {
        var resp = await _client.PostAsJsonAsync($"{ReportsBase}/trial-balance",
            new { chartId = "any-chart" });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ── seeding helpers ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a chart + a minimal set of GL accounts (Cash asset, Rental Income revenue, Advertising
    /// expense, AR) directly via the EF context, returning the chart id. Mirrors the production seed
    /// shape without depending on the seed-from-template route's account catalogue.
    /// </summary>
    private async Task<ChartOfAccountsId> SeedChartWithAccountsAsync()
    {
        var chartId = ChartOfAccountsId.NewId();
        await using var ctx = await _factory.CreateDbContextAsync();
        var chart = new ChartOfAccounts(
            Id:                       chartId,
            LegalEntityId:            new LegalEntityId("local-entity"),
            Name:                     "Reports Test Chart",
            BaseCurrency:             "USD",
            FiscalYearStartMonth:     1,
            FiscalYearStartDay:       1,
            RetainedEarningsAccountId: null,
            IsActive:                 true,
            CreatedAtUtc:             new Instant(System.TimeProvider.System.GetUtcNow()),
            UpdatedAtUtc:             new Instant(System.TimeProvider.System.GetUtcNow()));
        ctx.Set<ChartOfAccounts>().Add(chart);
        ctx.Set<GLAccount>().AddRange(
            GLAccount.Create(GLAccountId.NewId(), chartId, "1100", "Cash", GLAccountType.Asset,
                AccountSubtype.BankAccount, "USD", new Instant(System.TimeProvider.System.GetUtcNow())),
            GLAccount.Create(GLAccountId.NewId(), chartId, "1130", "Accounts Receivable", GLAccountType.Asset,
                AccountSubtype.AccountsReceivable, "USD", new Instant(System.TimeProvider.System.GetUtcNow())),
            GLAccount.Create(GLAccountId.NewId(), chartId, "4100", "Rental Income", GLAccountType.Revenue,
                AccountSubtype.OperatingIncome, "USD", new Instant(System.TimeProvider.System.GetUtcNow())),
            GLAccount.Create(GLAccountId.NewId(), chartId, "5100", "Advertising", GLAccountType.Expense,
                AccountSubtype.OperatingExpense, "USD", new Instant(System.TimeProvider.System.GetUtcNow())));
        await ctx.SaveChangesAsync();
        return chartId;
    }

    /// <summary>Posts one balanced JE this month: Debit Cash 1000 / Credit Rental Income 1000.</summary>
    private async Task PostBalancedEntryAsync(ChartOfAccountsId chartId)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        var cash = await ctx.Set<GLAccount>().AsNoTracking().FirstAsync(a => a.ChartId == chartId && a.Code == "1100");
        var revenue = await ctx.Set<GLAccount>().AsNoTracking().FirstAsync(a => a.ChartId == chartId && a.Code == "4100");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var entry = new JournalEntry(
            id:           JournalEntryId.NewId(),
            tenantId:     LocalTenantId,
            entryDate:    today,
            memo:         "test",
            lines:        [new JournalEntryLine(cash.Id, 1000m, 0m), new JournalEntryLine(revenue.Id, 0m, 1000m)],
            createdAtUtc: new Instant(System.TimeProvider.System.GetUtcNow()))
        {
            ChartId = chartId,
            Status = JournalEntryStatus.Posted,
            SourceKind = JournalEntrySource.Manual,
        };
        ctx.Set<JournalEntry>().Add(entry);
        await ctx.SaveChangesAsync();
    }
}

/// <summary>T-576: direct report calls must pass the report-kind-and-chart authorization decision before any read.</summary>
public sealed class ReportsRouteTestsAuthorization
{
    private static readonly TenantId Tenant = ActiveTeamTenantContext.ProjectTenantId(NodeTestActiveTeam.TestTeamId);
    private const string PrivateChart = "private-report-chart-576";
    private const string PrivateCredential = "private-report-credential-576";

    public static TheoryData<string> Routes => new()
    {
        "/trial-balance", "/ar-aging-summary", "/ap-aging-summary", "/balance-sheet",
        "/profit-and-loss", "/profit-and-loss-by-property",
    };

    [Theory]
    [MemberData(nameof(Routes))]
    public Task Every_report_without_principal_renders_the_gate_denial(string route) =>
        AssertRefusedAsync(route, allowed: false, selectedTenant: null, hasClock: true,
            expectedRecordId: ReportRecordIdForRoute(route));

    [Theory]
    [MemberData(nameof(Routes))]
    public Task Every_report_with_a_malformed_credential_renders_the_gate_denial(string route) =>
        AssertRefusedAsync(route, allowed: false, selectedTenant: null, hasClock: true,
            credential: "not-a-valid-report-credential", expectedRecordId: ReportRecordIdForRoute(route));

    [Theory]
    [MemberData(nameof(Routes))]
    public Task Every_report_with_an_expired_credential_renders_the_gate_denial(string route) =>
        AssertRefusedAsync(route, allowed: false, selectedTenant: null, hasClock: true,
            credential: "expired-report-credential-576", expectedRecordId: ReportRecordIdForRoute(route));

    [Theory]
    [MemberData(nameof(Routes))]
    public Task Every_report_refuses_a_cross_tenant_selected_principal(string route) =>
        AssertRefusedAsync(route, allowed: true, new TenantId("foreign-report-tenant"), hasClock: true);

    [Theory]
    [MemberData(nameof(Routes))]
    public Task Every_report_without_a_clock_returns_Denied(string route) =>
        AssertRefusedAsync(route, allowed: true, Tenant, hasClock: false);

    [Fact]
    public Task Report_denial_is_audited_without_credentials_or_report_parameters() =>
        AssertRefusedAsync("/trial-balance", allowed: false, selectedTenant: null, hasClock: true, audit: true,
            expectedRecordId: ReportRecordIdForRoute("/trial-balance"));

    private static string ReportRecordIdForRoute(string route) =>
        ReportRecordId(route[1..], PrivateChart);

    private static string ReportRecordId(ReportKind kind, ChartOfAccountsId chartId) =>
        ReportRecordId(kind.ToKebab(), chartId.Value);

    private static string ReportRecordId(string kind, string chartId) =>
        $"{kind}:{Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(chartId)))}";

    private static async Task AssertRefusedAsync(string route, bool allowed, TenantId? selectedTenant,
        bool hasClock, bool audit = false, string credential = PrivateCredential, string? expectedRecordId = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var clock = new CountingClock();
        builder.Services.RemoveAll<TimeProvider>();
        if (hasClock) builder.Services.AddSingleton<TimeProvider>(clock);
        var decisions = new List<AuthorizationGateRequest>();
        builder.Services.AddSingleton(TestAuthorization.Gate(allowed, decisions.Add));
        if (audit)
        {
            builder.Services.AddSingleton(new NodePrincipalSigner(RandomNumberGenerator.GetBytes(32)));
            builder.Services.AddSingleton<IOperationSigner>(sp => sp.GetRequiredService<NodePrincipalSigner>().Signer);
            builder.Services.AddEnrollmentCompensatingControlAudit();
            builder.Services.AddSingleton<IAuditTrail>(sp => sp.GetRequiredService<InMemoryAuditTrail>());
            builder.Services.AddAuthorizationRefusalAudit();
        }
        await using var app = builder.Build();
        app.Use(async (http, next) =>
        {
            clock.Reset(); // Exclude the host's startup clock read from the request's authority read.
            http.Features.Set(DesktopPlaneRequestFeature.Instance);
            if (selectedTenant is { } tenant)
                http.Features.Set(new SelectedSessionRequestPrincipal("report-account", tenant,
                    new PrincipalUserId("report-user"), new CanonicalPartyReference("report-party"),
                    "report-membership", 1, [new PinnedGrantOwnerVersion("report-grant", 1)], 1,
                    "report-session", "report-coordination"));
            await next(http);
        });
        var runner = new UnreachableRunner();
        var factory = new UnreachableFactory();
        ReportsRoutes.Map(app.MapDeviceReachableProductDataGroup(), runner, factory, NodeTestActiveTeam.Accessor);
        await app.StartAsync();
        try
        {
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
            using var client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) };
            using var request = new HttpRequestMessage(HttpMethod.Post, ReportsRoutes.ReportsRouteBase + route)
            {
                Content = JsonContent.Create(new { chartId = PrivateChart, asOfDate = "2025-01-17" }),
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", credential);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            var wire = await response.Content.ReadAsStringAsync();
            using var body = JsonDocument.Parse(wire);
            var refusal = body.RootElement;
            Assert.Equal(AuthorizationRefusalRenderer.PermissionRequiredCode, refusal.GetProperty("code").GetString());
            Assert.Equal("reports:run", refusal.GetProperty("permission").GetString());
            Assert.Equal(audit
                    ? new[] { "auditId", "code", "detail", "permission", "remediation", "title" }
                    : new[] { "code", "detail", "permission", "remediation", "title" },
                refusal.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
            AssertRedacted(wire, credential);
            Assert.Equal(0, runner.Calls);
            Assert.Equal(0, factory.Calls);
            Assert.Equal(hasClock ? 1 : 0, clock.Reads);
            if (hasClock && selectedTenant is null)
            {
                var decision = Assert.Single(decisions);
                Assert.Equal(Tenant, decision.Tenant);
                Assert.Equal("reports:run", decision.Act.Operation.Value);
                Assert.Equal(TestAuthorization.At, decision.At);
                if (expectedRecordId is not null)
                {
                    Assert.Equal("reports", decision.Target.RecordKind);
                    Assert.Equal(expectedRecordId, decision.Target.RecordId);
                    Assert.Equal($"/records/{expectedRecordId}", decision.Target.Scope.ToString());
                }
            }
            else Assert.Empty(decisions);

            if (!hasClock)
            {
                var expected = JsonSerializer.SerializeToElement(
                    ((IValueHttpResult)RequestAuthorization.Denied("reports:run")).Value);
                Assert.Equal(expected.GetRawText(), refusal.GetRawText());
            }
            if (audit)
            {
                var rows = new List<AuditRecord>();
                await foreach (var row in app.Services.GetRequiredService<IAuditTrail>().QueryAsync(
                    new AuditQuery(Tenant, AuthorizationRefusalAudit.AuthorizationRefusedEventType))) rows.Add(row);
                var recorded = Assert.Single(rows);
                Assert.Equal(recorded.AuditId, refusal.GetProperty("auditId").GetGuid());
                Assert.Equal(false, recorded.Payload.Payload.Body["preDecision"]);
                Assert.NotNull(recorded.AuthoritySnapshot);
                AssertRedacted(JsonSerializer.Serialize(recorded));
            }
        }
        finally { await app.StopAsync(); }
    }

    private static void AssertRedacted(string text, params string[] credentials)
    {
        foreach (var secret in new[] { PrivateChart, PrivateCredential, "chartId", "asOfDate", "2025-01-17" }
            .Concat(credentials))
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
    }

    private sealed class CountingClock : TimeProvider
    {
        public int Reads { get; private set; }
        public void Reset() => Reads = 0;
        public override DateTimeOffset GetUtcNow()
        {
            Reads++;
            return TestAuthorization.At;
        }
    }

    private sealed class UnreachableFactory : IDbContextFactory<LocalNodeDbContext>
    {
        public int Calls { get; private set; }
        public LocalNodeDbContext CreateDbContext()
        {
            Calls++;
            throw new InvalidOperationException("Refused reports must not read the chart.");
        }
    }

    private sealed class UnreachableRunner : IReportRunner
    {
        public int Calls { get; private set; }
        public Task<ReportRunResult<TResult>> RunAsync<TParams, TResult>(ReportKind kind, TParams parameters,
            TenantId tenantId, PrincipalId requestedBy, CancellationToken ct = default)
            where TParams : class where TResult : class
        {
            Calls++;
            throw new InvalidOperationException("Refused reports must not run.");
        }
    }
}
