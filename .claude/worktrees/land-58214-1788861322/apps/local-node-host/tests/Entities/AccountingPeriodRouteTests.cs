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
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Health;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// T1 local-first sweep — route-level tests for the node-local accounting-periods surface
/// (list + open/close) over the EXISTING <c>fiscal_periods</c>/<c>fiscal_years</c> tables.
/// </summary>
/// <remarks>
/// Hosts the SAME route handler <see cref="HostedAccountingPeriodApiEndpoint"/> registers, on a
/// real in-process Kestrel listener over a temp SQLite store. The entity + seed routes are
/// co-registered so each test can stand up a seeded chart (as onboarding does) before driving the
/// period routes.
/// </remarks>
public sealed class AccountingPeriodRouteTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private string _dir = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        _dir = Path.Combine(Path.GetTempPath(), "harborline-periods-routes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var connectionString = $"Data Source={Path.Combine(_dir, "periods-test.db")};Pooling=False";

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
        AccountingPeriodRoutes.Map(deviceReachable, new NodeAccountingPeriodService(factory, TimeProvider.System));

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
    private const string PeriodsRoute = "/api/local-node/accounting-periods";

    private async Task SeedChartAsync()
    {
        var entResp = await _client.PostAsJsonAsync(EntityRoute, new { legalName = "Periods Test LLC" });
        entResp.EnsureSuccessStatusCode();
        var entityId = (await entResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var seedResp = await _client.PostAsJsonAsync(SeedRoute, new { entityId, templateId = "rental-real-estate" });
        seedResp.EnsureSuccessStatusCode();
    }

    [Fact(DisplayName = "Periods list: pre-seed returns empty list + null chartId (renders offline pre-onboarding)")]
    public async Task List_PreSeed_EmptyNullChart()
    {
        var resp = await _client.GetAsync(PeriodsRoute);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, doc.GetProperty("chartId").ValueKind);
        Assert.Empty(doc.GetProperty("periods").EnumerateArray());
    }

    [Fact(DisplayName = "Periods open: fresh install opens an OPEN period covering today (the offline first-run fix)")]
    public async Task Open_FreshInstall_CreatesOpenPeriod()
    {
        await SeedChartAsync();

        var resp = await _client.PostAsJsonAsync($"{PeriodsRoute}/open", new { });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Open", doc.GetProperty("status").GetString());

        // The period covers today.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var start = DateOnly.Parse(doc.GetProperty("startDate").GetString()!);
        var end = DateOnly.Parse(doc.GetProperty("endDate").GetString()!);
        Assert.True(start <= today && today <= end);

        // It now appears in the list under the seeded chart.
        var list = await (await _client.GetAsync(PeriodsRoute)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrWhiteSpace(list.GetProperty("chartId").GetString()));
        Assert.Single(list.GetProperty("periods").EnumerateArray());
    }

    [Fact(DisplayName = "Periods open: idempotent — opening twice for the same date returns the same open period")]
    public async Task Open_Twice_Idempotent()
    {
        await SeedChartAsync();
        var first = await (await _client.PostAsJsonAsync($"{PeriodsRoute}/open", new { date = "2026-03-15" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var second = await (await _client.PostAsJsonAsync($"{PeriodsRoute}/open", new { date = "2026-03-20" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        // Both dates fall in 2026-03 → same period id, no duplicate.
        Assert.Equal(first.GetProperty("id").GetString(), second.GetProperty("id").GetString());

        var list = await (await _client.GetAsync(PeriodsRoute)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Single(list.GetProperty("periods").EnumerateArray());
    }

    [Fact(DisplayName = "Periods open: pre-seed (no chart) returns 400 no_chart")]
    public async Task Open_NoChart_Returns400()
    {
        var resp = await _client.PostAsJsonAsync($"{PeriodsRoute}/open", new { });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact(DisplayName = "Periods close: Open → SoftClosed by default")]
    public async Task Close_SoftCloses()
    {
        await SeedChartAsync();
        var open = await (await _client.PostAsJsonAsync($"{PeriodsRoute}/open", new { date = "2026-05-10" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var id = open.GetProperty("id").GetString()!;

        var resp = await _client.PostAsJsonAsync($"{PeriodsRoute}/{id}/close", new { });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("SoftClosed", doc.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, doc.GetProperty("softClosedAt").ValueKind);
    }

    [Fact(DisplayName = "Periods close: lock=true → Locked (auto-soft-closes inline)")]
    public async Task Close_Lock_Locks()
    {
        await SeedChartAsync();
        var open = await (await _client.PostAsJsonAsync($"{PeriodsRoute}/open", new { date = "2026-06-10" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var id = open.GetProperty("id").GetString()!;

        var resp = await _client.PostAsJsonAsync($"{PeriodsRoute}/{id}/close", new { @lock = true });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Locked", doc.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, doc.GetProperty("lockedAt").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, doc.GetProperty("softClosedAt").ValueKind);
    }

    [Fact(DisplayName = "Periods open: reopens a SoftClosed covering period back to Open")]
    public async Task Open_ReopensSoftClosed()
    {
        await SeedChartAsync();
        var open = await (await _client.PostAsJsonAsync($"{PeriodsRoute}/open", new { date = "2026-07-10" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var id = open.GetProperty("id").GetString()!;
        await _client.PostAsJsonAsync($"{PeriodsRoute}/{id}/close", new { }); // soft-close

        // Re-open for a date in the same month → reopens the same (now SoftClosed) period.
        var reopen = await _client.PostAsJsonAsync($"{PeriodsRoute}/open", new { date = "2026-07-20" });
        Assert.Equal(HttpStatusCode.OK, reopen.StatusCode);
        var doc = await reopen.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(id, doc.GetProperty("id").GetString());
        Assert.Equal("Open", doc.GetProperty("status").GetString());
    }

    [Fact(DisplayName = "Periods open: a Locked covering period is NOT auto-reopened (400 period_locked)")]
    public async Task Open_LockedPeriod_Returns400()
    {
        await SeedChartAsync();
        var open = await (await _client.PostAsJsonAsync($"{PeriodsRoute}/open", new { date = "2026-08-10" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var id = open.GetProperty("id").GetString()!;
        await _client.PostAsJsonAsync($"{PeriodsRoute}/{id}/close", new { @lock = true }); // lock

        var resp = await _client.PostAsJsonAsync($"{PeriodsRoute}/open", new { date = "2026-08-20" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact(DisplayName = "Periods close: unknown id returns 404")]
    public async Task Close_UnknownId_Returns404()
    {
        await SeedChartAsync();
        var resp = await _client.PostAsJsonAsync($"{PeriodsRoute}/{Guid.NewGuid():N}/close", new { });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
