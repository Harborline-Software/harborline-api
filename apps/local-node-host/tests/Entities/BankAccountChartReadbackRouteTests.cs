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

using Harborline.Api.Blocks.Banking.Data;
using Harborline.Api.Blocks.Banking.Models;
using Harborline.Api.Blocks.Banking.Matching;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialLedger.Services;
using Harborline.Api.Blocks.FinancialPeriods.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Integrations.Payments;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Banking;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Packs;

using Xunit;

using BankingServices = (
    Harborline.Api.Blocks.Banking.Services.IBankAccountRepository AccountRepo,
    Harborline.Api.Blocks.Banking.Services.IStatementLineRepository LineRepo,
    Harborline.Api.Blocks.Banking.Services.IMatchLinkRepository LinkRepo,
    Harborline.Api.Blocks.Banking.Services.IReconciliationRepository ReconciliationRepo,
    Harborline.Api.Blocks.FinancialPeriods.Services.IFiscalPeriodRepository PeriodRepo,
    Harborline.Api.Blocks.Banking.Import.ImportPipelineService ImportPipeline,
    Harborline.Api.Blocks.Banking.Matching.AcceptMatchService AcceptMatchService,
    Harborline.Api.Blocks.Banking.Matching.UnMatchService UnMatchService,
    Harborline.Api.Blocks.Banking.Matching.ReconciliationLockLease ReconciliationLease,
    Harborline.Api.Blocks.Banking.Feed.IBankFeedProvider FeedProvider,
    Microsoft.EntityFrameworkCore.IDbContextFactory<Harborline.Api.LocalNodeHost.Data.Banking.NodeLocalBankFeedDbContext> FeedConnectionFactory);

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// T-1049: a bank account created through <c>POST /api/local-node/bank-accounts</c> names no chart of accounts, and
/// it must still read back by id and by list from the real SQLite store. Accounts already stored with a JSON-null
/// chart id must read too.
/// </summary>
public sealed class BankAccountChartReadbackRouteTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private string _dir = null!;
    private IDbContextFactory<LocalNodeDbContext> _factory = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        Harborline.Api.LocalNodeHost.Tests.Authorization.TestDesktopOperator.AddTestDesktopOperator(builder.Services);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        _dir = Path.Combine(Path.GetTempPath(), "harborline-bank-chart-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        builder.Services.AddSingleton<IHarborlineEntityModule, BankingEntityModule>();
        builder.Services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialLedger.Data.FinancialLedgerEntityModule>();
        builder.Services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialPeriods.Data.FinancialPeriodsEntityModule>();
        builder.Services.AddDbContextFactory<LocalNodeDbContext>(opt =>
            opt.UseSqlite($"Data Source={Path.Combine(_dir, "bank.db")};Pooling=False"));
        builder.Services.AddDbContextFactory<NodeLocalBankFeedDbContext>(opt =>
            opt.UseSqlite($"Data Source={Path.Combine(_dir, "bank-feed.db")};Pooling=False"));

        var authorization = new MutableAuthorizationContext();
        authorization.Allow(TeamRolePermissions.RecordsWrite);
        builder.Services.AddSingleton<IAuthorizationContext>(authorization);
        builder.Services.AddTestKernelClock();
        builder.Services.AddSingleton(Harborline.Api.LocalNodeHost.Tests.Authorization.TestRouteGate.Following(
            permission => authorization.HasPermission(permission)));

        _app = builder.Build();
        _factory = _app.Services.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
        var feedFactory = _app.Services.GetRequiredService<IDbContextFactory<NodeLocalBankFeedDbContext>>();
        await using (var ctx = await _factory.CreateDbContextAsync())
            await ctx.Database.EnsureCreatedAsync();
        await using (var feed = await feedFactory.CreateDbContextAsync())
            await feed.Database.EnsureCreatedAsync();

        _app.Use(async (http, next) =>
        {
            http.Features.Set(DesktopPlaneRequestFeature.Instance);
            await next(http);
        });

        var accounts = new NodeEfBankAccountRepository(_factory);
        var lines = new NodeEfStatementLineRepository(_factory);
        var links = new NodeEfMatchLinkRepository(_factory);
        var reconciliations = new NodeEfReconciliationRepository(_factory);
        var periods = new NodeEfFiscalPeriodRepository(_factory);
        BankingServices banking = (
            accounts, lines, links, reconciliations, periods, null!,
            new AcceptMatchService(links, lines, reconciliations, periods, TimeProvider.System,
                new ReconciliationLockLease(TimeProvider.System)), null!, null!, null!,
            feedFactory);
        BankAccountRoutes.Map(
            _app.MapDeviceReachableProductDataGroup(),
            banking,
            NodeTestActiveTeam.Accessor,
            new NodeBankAccountWriter(accounts, Authorization.TestAuthorization.AllowGate()),
            TimeProvider.System,
            new NodeEfAccountResolver(_factory));

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
    [InlineData(false, "bank_account_chart_unresolved")]
    [InlineData(true, "statement_line_account_mismatch")]
    public async Task AcceptMatch_RefusesUnresolvedChartOrAnotherAccountsLine(bool wrongAccount, string expectedError)
    {
        var account = await CreateAccountAsync("owner");
        var selected = wrongAccount ? await CreateAccountAsync("other") : account;
        var link = await ProposeAsync(account);

        var response = await _client.PostAsJsonAsync($"{BankAccountRoutes.RouteBase}/{selected.Value}/accept-match", new { matchLinkId = link.Value });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(expectedError, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.Equal(MatchLinkState.Proposed, (await new NodeEfMatchLinkRepository(_factory).GetByIdAsync(NodeTenant.Resolve(NodeTestActiveTeam.Accessor), link))!.State);
    }

    [Fact]
    public async Task AcceptMatch_ResolvesNullChartThroughLedgerAccount_AndHonorsLockedPeriod()
    {
        // The shipping listener has an inner container without this service; the outer resolver is explicit.
        Assert.Null(_app.Services.GetService<IAccountResolver>());
        var account = await CreateAccountAsync("owner");
        var link = await ProposeAsync(account);
        var chart = new ChartOfAccountsId("match-chart");
        var now = new Instant(DateTimeOffset.Parse("2026-10-02T12:00:00Z"));
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.Set<GLAccount>().Add(new GLAccount(new GLAccountId("match-gl"), "1010", "Cash", GLAccountType.Asset, ChartId: chart));
            db.Set<FiscalPeriod>().Add(FiscalPeriod.CreateOpen(new FiscalPeriodId("match-period"), chart,
                new FiscalYearId("match-year"), FiscalPeriodKind.Monthly, "October", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), now)
                with { Status = FiscalPeriodStatus.Locked, LockedAtUtc = now });
            await db.SaveChangesAsync();
        }

        var response = await _client.PostAsJsonAsync($"{BankAccountRoutes.RouteBase}/{account.Value}/accept-match", new { matchLinkId = link.Value });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var refusal = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("accept_rejected", refusal.GetProperty("error").GetString());
        Assert.Equal("FiscalPeriodLocked", refusal.GetProperty("reason").GetString());
        Assert.Equal(MatchLinkState.Proposed, (await new NodeEfMatchLinkRepository(_factory).GetByIdAsync(NodeTenant.Resolve(NodeTestActiveTeam.Accessor), link))!.State);
    }

    private async Task<BankAccountId> CreateAccountAsync(string name)
    {
        var response = await _client.PostAsJsonAsync(BankAccountRoutes.RouteBase, new { displayName = name, linkedLedgerAccountId = "match-gl" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return new BankAccountId((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!);
    }

    private async Task<MatchLinkId> ProposeAsync(BankAccountId account)
    {
        var tenant = NodeTenant.Resolve(NodeTestActiveTeam.Accessor);
        var now = new Instant(DateTimeOffset.Parse("2026-10-02T12:00:00Z"));
        var line = StatementLineId.NewId();
        var link = MatchLinkId.NewId();
        await new NodeEfStatementLineRepository(_factory).AddAsync(new StatementLine(line, tenant, account, null, now,
            125m, new CurrencyCode("USD"), "Deposit", false, ReconciliationState.Proposed,
            new ImportSourceRef(ImportSourceKind.FileImport, "match-batch", 0), null, now));
        await new NodeEfMatchLinkRepository(_factory).AddAsync(new MatchLink(link, tenant, line,
            new LedgerTransactionRef(new JournalEntryId("match-entry")), 125m, MatchLinkState.Proposed, null));
        return link;
    }

    [Fact(DisplayName = "T-1049: an account created through the route reads back by id and by list from the SQLite store")]
    [Trait("Holds", "kernel-core-ck-10")]
    public async Task CreatedAccount_ReadsBack_ByIdAndByList()
    {
        var created = await _client.PostAsJsonAsync(BankAccountRoutes.RouteBase, new { displayName = "Ops Checking" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var byId = await _client.GetAsync(created.Headers.Location);
        var list = await _client.GetAsync(BankAccountRoutes.RouteBase);

        Assert.Equal(HttpStatusCode.OK, byId.StatusCode);
        Assert.Equal("Ops Checking", (await byId.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("displayName").GetString());
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var listed = (await list.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items");
        Assert.Equal("Ops Checking", Assert.Single(listed.EnumerateArray()).GetProperty("displayName").GetString());
    }

    [Fact(DisplayName = "T-1049: an account stored before the fix with a JSON-null chart id reads by id and by list")]
    public async Task StoredNullChartRow_ReadsBack()
    {
        var created = await _client.PostAsJsonAsync(BankAccountRoutes.RouteBase, new { displayName = "Legacy Checking" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        // The exact bytes the route stored before T-1049, written straight into the column.
        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(1, await ctx.Database.ExecuteSqlRawAsync(
                "UPDATE bank_accounts SET linked_ledger_account_json = {0}",
                "{\"GLAccountId\":\"1010\",\"ChartId\":null}"));
        }

        var byId = await _client.GetAsync(created.Headers.Location);
        var list = await _client.GetAsync(BankAccountRoutes.RouteBase);

        Assert.Equal(HttpStatusCode.OK, byId.StatusCode);
        var detail = await byId.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Legacy Checking", detail.GetProperty("displayName").GetString());
        Assert.Equal("1010", detail.GetProperty("linkedLedgerAccountId").GetString());
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var listed = (await list.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items");
        Assert.Equal("Legacy Checking", Assert.Single(listed.EnumerateArray()).GetProperty("displayName").GetString());
    }
}
