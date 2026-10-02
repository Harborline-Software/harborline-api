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
        BankingServices banking = (
            accounts, new NodeEfStatementLineRepository(_factory), null!, null!, null!, null!, null!, null!, null!, null!,
            feedFactory);
        BankAccountRoutes.Map(
            _app.MapDeviceReachableProductDataGroup(),
            banking,
            NodeTestActiveTeam.Accessor,
            new NodeBankAccountWriter(accounts, Authorization.TestAuthorization.AllowGate()),
            TimeProvider.System);

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
