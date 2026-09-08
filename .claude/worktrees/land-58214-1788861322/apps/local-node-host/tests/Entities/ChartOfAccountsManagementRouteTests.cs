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

using Harborline.Api.Blocks.FinancialLedger.Data;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Health;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// T1 local-first sweep — route-level tests for the node-local chart-of-accounts
/// MANAGEMENT surface (list / detail / create / archive).
/// </summary>
/// <remarks>
/// Hosts the SAME route handlers <see cref="HostedChartOfAccountsApiEndpoint"/> registers,
/// on a real in-process Kestrel listener over a temp SQLite store. The entity + seed routes
/// are co-registered so each test can stand up an entity + a seeded chart (exactly as the
/// onboarding wizard does) before driving the management routes.
/// </remarks>
public sealed class ChartOfAccountsManagementRouteTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private string _dir = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        _dir = Path.Combine(Path.GetTempPath(), "harborline-coa-mgmt-routes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        // Pooling=False: this fixture's connections never enter the process-global SQLite handle pool, so a
        // SIBLING test class's SqliteConnection.ClearAllPools() (a process-wide op) can never dispose a
        // handle this fixture's Open() is mid-use. That race produced an intermittent
        // ObjectDisposedException('SQLitePCL.sqlite3') during EnsureCreatedAsync on unrelated PRs
        // (bug-20260702-8012e553). Non-pooled connections also release the file on Close, so the temp DB
        // can be deleted below without a global ClearAllPools() (which was itself a source of the race).
        var connectionString = $"Data Source={Path.Combine(_dir, "coa-mgmt-test.db")};Pooling=False";

        builder.Services.AddSingleton<IHarborlineEntityModule, FinancialLedgerEntityModule>();
        // Ticket 151: the entity POST is now permission-gated; grant-all keeps these route tests on their pre-gate behaviour.
        builder.Services.AddSingleton<Harborline.Api.Foundation.Authorization.IAuthorizationContext>(Harborline.Api.LocalNodeHost.Tests.Packs.TestAuthorizationContext.AllowAll());
        // Ticket 205 slice 4: the record-scoped route guards resolve at the gate, so the host that
        // registers an allow-all authorization context registers the matching allow-all gate and a clock.
        builder.Services.AddTestKernelClock();
        builder.Services.AddSingleton(Harborline.Api.LocalNodeHost.Tests.Authorization.TestRouteGate.AllowAll());
        builder.Services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialPeriods.Data.FinancialPeriodsEntityModule>();
        builder.Services.AddDbContextFactory<LocalNodeDbContext>(opt =>
            opt.UseSqlite(connectionString));

        _app = builder.Build();

        var factory = _app.Services.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
        await using (var ctx = await factory.CreateDbContextAsync())
        {
            await ctx.Database.EnsureCreatedAsync();
        }

        _app.Use(async (http, next) =>
        {
            http.Features.Set(DesktopPlaneRequestFeature.Instance);
            await next(http);
        });
        var deviceReachable = _app.MapDeviceReachableProductDataGroup();
        EntityRoutes.Map(deviceReachable, factory, NodeTestActiveTeam.Accessor,
            new Data.Entities.NodeEntityWriter(factory, Harborline.Api.Foundation.Assets.Entities.NullEntityValidator.Instance, Authorization.TestAuthorization.AllowGate()),
            TimeProvider.System);
        ChartOfAccountsRoutes.Map(deviceReachable, factory, NodeTestActiveTeam.Accessor, TimeProvider.System);
        ChartOfAccountsManagementRoutes.Map(deviceReachable, factory, TimeProvider.System);

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
        // No SqliteConnection.ClearAllPools() here: with Pooling=False the connections are already closed
        // (handles destroyed, file released) once the app is disposed, so a process-global pool clear is
        // both unnecessary and a cross-test race hazard (bug-20260702-8012e553).
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    private const string EntityRoute = "/api/local-node/entities";
    private const string SeedRoute = "/api/local-node/chart-of-accounts/seed-from-template";
    private const string MgmtRoute = "/api/local-node/chart-of-accounts";

    private async Task<string> CreateEntityAsync(string legalName = "Mgmt Test LLC")
    {
        var resp = await _client.PostAsJsonAsync(EntityRoute, new { legalName });
        resp.EnsureSuccessStatusCode();
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return doc.GetProperty("id").GetString()!;
    }

    /// <summary>Seeds a chart from the rental-real-estate template and returns its chartId.</summary>
    private async Task<string> SeedChartAsync()
    {
        var entityId = await CreateEntityAsync();
        var resp = await _client.PostAsJsonAsync(SeedRoute, new { entityId, templateId = "rental-real-estate" });
        resp.EnsureSuccessStatusCode();
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return doc.GetProperty("chartId").GetString()!;
    }

    [Fact(DisplayName = "COA list: pre-seed returns empty list + null chartId (page renders offline before onboarding)")]
    public async Task List_PreSeed_ReturnsEmptyListNullChart()
    {
        var resp = await _client.GetAsync(MgmtRoute);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, doc.GetProperty("chartId").ValueKind);
        Assert.Empty(doc.GetProperty("accounts").EnumerateArray());
    }

    [Fact(DisplayName = "COA list: after seed returns the seeded accounts under the seeded chartId")]
    public async Task List_AfterSeed_ReturnsAccounts()
    {
        var chartId = await SeedChartAsync();

        var resp = await _client.GetAsync(MgmtRoute);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(chartId, doc.GetProperty("chartId").GetString());
        var accounts = doc.GetProperty("accounts").EnumerateArray().ToList();
        Assert.NotEmpty(accounts);
        // Ordered by code ascending.
        var codes = accounts.Select(a => a.GetProperty("code").GetString()!).ToList();
        Assert.Equal(codes.OrderBy(c => c, StringComparer.Ordinal), codes);
    }

    [Fact(DisplayName = "COA create: a valid account is created and appears in the list")]
    public async Task Create_ValidAccount_Created()
    {
        await SeedChartAsync();

        var resp = await _client.PostAsJsonAsync(MgmtRoute, new
        {
            code = "8100",
            name = "Custom Income",
            type = "Revenue",
            subtype = "OperatingIncome",
            currency = "USD",
        });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("8100", doc.GetProperty("code").GetString());
        Assert.True(doc.GetProperty("isActive").GetBoolean());
        var id = doc.GetProperty("id").GetString()!;

        // It is retrievable by detail.
        var detail = await _client.GetAsync($"{MgmtRoute}/{id}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
    }

    [Fact(DisplayName = "COA create: pre-seed (no chart) returns 400 no_chart")]
    public async Task Create_NoChart_Returns400()
    {
        var resp = await _client.PostAsJsonAsync(MgmtRoute, new
        {
            code = "4100",
            name = "X",
            type = "Revenue",
            subtype = "OperatingIncome",
            currency = "USD",
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact(DisplayName = "COA create: duplicate code (collides with a seeded account) returns 400 not 500")]
    public async Task Create_DuplicateCode_Returns400()
    {
        await SeedChartAsync();
        // 4100 is in the rental-real-estate template — a duplicate must 400, never 500.
        var resp = await _client.PostAsJsonAsync(MgmtRoute, new
        {
            code = "4100",
            name = "Dup",
            type = "Revenue",
            subtype = "OperatingIncome",
            currency = "USD",
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact(DisplayName = "COA create: invalid type returns 400")]
    public async Task Create_InvalidType_Returns400()
    {
        await SeedChartAsync();
        var resp = await _client.PostAsJsonAsync(MgmtRoute, new
        {
            code = "9999",
            name = "Bad",
            type = "NotAType",
            subtype = "OperatingIncome",
            currency = "USD",
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact(DisplayName = "COA create: 2-letter currency returns 400")]
    public async Task Create_BadCurrency_Returns400()
    {
        await SeedChartAsync();
        var resp = await _client.PostAsJsonAsync(MgmtRoute, new
        {
            code = "9998",
            name = "Bad Currency",
            type = "Revenue",
            subtype = "OperatingIncome",
            currency = "US",
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact(DisplayName = "COA archive: archives an account (IsActive=false) and is idempotent")]
    public async Task Archive_Account_SoftDeletesIdempotent()
    {
        await SeedChartAsync();
        var createResp = await _client.PostAsJsonAsync(MgmtRoute, new
        {
            code = "8200",
            name = "Misc Income",
            type = "Revenue",
            subtype = "OperatingIncome",
            currency = "USD",
        });
        createResp.EnsureSuccessStatusCode();
        var id = (await createResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var archiveResp = await _client.PostAsync($"{MgmtRoute}/{id}/archive", content: null);
        Assert.Equal(HttpStatusCode.OK, archiveResp.StatusCode);
        var archivedDoc = await archiveResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(archivedDoc.GetProperty("isActive").GetBoolean());

        // Idempotent — a second archive still returns 200 + inactive.
        var archiveResp2 = await _client.PostAsync($"{MgmtRoute}/{id}/archive", content: null);
        Assert.Equal(HttpStatusCode.OK, archiveResp2.StatusCode);
        Assert.False((await archiveResp2.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("isActive").GetBoolean());

        // Default list (active-only) no longer contains it; includeInactive=true does.
        var activeList = await (await _client.GetAsync(MgmtRoute)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.DoesNotContain(
            activeList.GetProperty("accounts").EnumerateArray(),
            a => a.GetProperty("id").GetString() == id);

        var allList = await (await _client.GetAsync($"{MgmtRoute}?includeInactive=true")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(
            allList.GetProperty("accounts").EnumerateArray(),
            a => a.GetProperty("id").GetString() == id);
    }

    [Fact(DisplayName = "COA detail: unknown id returns 404")]
    public async Task Detail_UnknownId_Returns404()
    {
        await SeedChartAsync();
        var resp = await _client.GetAsync($"{MgmtRoute}/{Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact(DisplayName = "COA archive: unknown id returns 404")]
    public async Task Archive_UnknownId_Returns404()
    {
        await SeedChartAsync();
        var resp = await _client.PostAsync($"{MgmtRoute}/{Guid.NewGuid():N}/archive", content: null);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
