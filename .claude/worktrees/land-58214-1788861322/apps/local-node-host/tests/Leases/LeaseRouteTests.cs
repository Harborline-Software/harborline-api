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

using Harborline.Api.LocalNodeHost.Data.Leases;
using Harborline.Api.LocalNodeHost.Health;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Leases;

/// <summary>
/// ADR 0115 D8 Stage 2 Cohort C — route-level tests for the node-local lease API.
/// Mirrors <c>MaintenanceRouteTests</c>.
/// </summary>
public sealed class LeaseRouteTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private string _baseUrl = null!;
    private string _dir = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        _dir = Path.Combine(Path.GetTempPath(), "harborline-lease-routes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var connectionString = $"Data Source={Path.Combine(_dir, "lease.db")};Pooling=False";
        builder.Services.AddDbContextFactory<NodeLocalLeaseDbContext>(opt =>
            opt.UseSqlite(connectionString));

        _app = builder.Build();

        var factory = _app.Services.GetRequiredService<IDbContextFactory<NodeLocalLeaseDbContext>>();
        await using (var ctx = await factory.CreateDbContextAsync())
        {
            await ctx.Database.MigrateAsync();
        }

        _app.Use(async (http, next) =>
        {
            http.Features.Set(DesktopPlaneRequestFeature.Instance);
            await next(http);
        });
        LeaseRoutes.Map(_app.MapDeviceReachableProductDataGroup(), factory, TimeProvider.System);

        await _app.StartAsync();

        var addresses = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>();
        _baseUrl = addresses!.Addresses.First();
        _client = new HttpClient { BaseAddress = new Uri(_baseUrl) };
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    private const string Route = "/api/local-node/leases";

    [Fact(DisplayName = "Lease route: list is empty on a fresh store")]
    public async Task List_Empty_OnFreshStore()
    {
        var doc = await _client.GetFromJsonAsync<JsonElement>(Route);
        Assert.Equal(0, doc.GetProperty("data").GetArrayLength());
    }

    [Fact(DisplayName = "Lease route: create → flat Lease shape + server-assigned name")]
    public async Task Create_ReturnsFlatShape_WithAssignedName()
    {
        var resp = await _client.PostAsJsonAsync(Route, new
        {
            tenant = "Jane Tenant",
            property = "PROP-0001",
            unit = "4B",
            start_date = "2026-01-01",
            end_date = "2026-12-31",
            monthly_rent = 1850.00m,
            status = "Active",
            company = "Acme Holdings",
            termCadence = "monthly",
            autoRenew = true,
        });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var lease = doc.GetProperty("data");

        Assert.False(string.IsNullOrWhiteSpace(lease.GetProperty("name").GetString()));
        Assert.Equal("Jane Tenant", lease.GetProperty("tenant").GetString());
        Assert.Equal("PROP-0001", lease.GetProperty("property").GetString());
        Assert.Equal("4B", lease.GetProperty("unit").GetString());
        Assert.Equal(1850.00m, lease.GetProperty("monthly_rent").GetDecimal());
        Assert.Equal("Active", lease.GetProperty("status").GetString());
        Assert.Equal("monthly", lease.GetProperty("termCadence").GetString());
        Assert.True(lease.GetProperty("autoRenew").GetBoolean());
    }

    [Fact(DisplayName = "Lease route: create defaults status=Active and termCadence=fixed")]
    public async Task Create_Defaults_StatusAndCadence()
    {
        var resp = await _client.PostAsJsonAsync(Route, new { tenant = "Minimal Tenant" });
        var lease = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal("Active", lease.GetProperty("status").GetString());
        Assert.Equal("fixed", lease.GetProperty("termCadence").GetString());
        Assert.False(lease.GetProperty("autoRenew").GetBoolean());
    }

    [Fact(DisplayName = "Lease route: create rejects missing tenant (400)")]
    public async Task Create_RejectsMissingTenant()
    {
        var resp = await _client.PostAsJsonAsync(Route, new { property = "PROP-1" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact(DisplayName = "Lease route: create rejects invalid status (400)")]
    public async Task Create_RejectsInvalidStatus()
    {
        var resp = await _client.PostAsJsonAsync(Route, new { tenant = "x", status = "Pending" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact(DisplayName = "Lease route: create rejects invalid termCadence (400)")]
    public async Task Create_RejectsInvalidCadence()
    {
        var resp = await _client.PostAsJsonAsync(Route, new { tenant = "x", termCadence = "fortnightly" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact(DisplayName = "Lease route: create → get round-trips over HTTP")]
    public async Task Create_Get_RoundTrips()
    {
        var created = await _client.PostAsJsonAsync(Route, new { tenant = "Roundtrip Tenant", property = "PROP-2" });
        var name = (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("data").GetProperty("name").GetString();

        var got = await _client.GetFromJsonAsync<JsonElement>($"{Route}/{name}");
        Assert.Equal("Roundtrip Tenant", got.GetProperty("data").GetProperty("tenant").GetString());
    }

    [Fact(DisplayName = "Lease route: get missing → 404")]
    public async Task Get_Missing_Returns404()
    {
        var resp = await _client.GetAsync($"{Route}/LEASE-99999");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
