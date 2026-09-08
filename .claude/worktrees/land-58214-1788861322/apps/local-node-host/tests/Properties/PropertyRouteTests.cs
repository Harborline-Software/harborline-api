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

using Harborline.Api.LocalNodeHost.Data.Properties;
using Harborline.Api.LocalNodeHost.Health;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Properties;

/// <summary>
/// ADR 0115 D8 Stage 2 Cohort C — route-level tests for the node-local property
/// API. Hosts the SAME production route handlers
/// <see cref="HostedPropertyApiEndpoint"/> registers, on a real in-process
/// Kestrel listener (ephemeral loopback port), backed by a temp SQLite file, and
/// drives them with a real <see cref="HttpClient"/>. Proves the flat
/// <c>Property</c> wire contract end-to-end. Mirrors <c>MaintenanceRouteTests</c>.
/// </summary>
public sealed class PropertyRouteTests : IAsyncLifetime
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

        // A real on-disk temp SQLite file — a file-backed store persists across
        // the factory's per-request connections. (SC-1 encryption is covered by
        // NodeLocalStoreTests; this test focuses on route behaviour.)
        _dir = Path.Combine(Path.GetTempPath(), "harborline-prop-routes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var connectionString = $"Data Source={Path.Combine(_dir, "prop.db")};Pooling=False";
        builder.Services.AddDbContextFactory<NodeLocalPropertyDbContext>(opt =>
            opt.UseSqlite(connectionString));

        _app = builder.Build();

        var factory = _app.Services.GetRequiredService<IDbContextFactory<NodeLocalPropertyDbContext>>();
        await using (var ctx = await factory.CreateDbContextAsync())
        {
            await ctx.Database.MigrateAsync();
        }

        // Map the SAME production routes (mirrors HostedPropertyApiEndpoint wiring —
        // no [FromServices]).
        _app.Use(async (http, next) =>
        {
            http.Features.Set(DesktopPlaneRequestFeature.Instance);
            await next(http);
        });
        PropertyRoutes.Map(_app.MapDeviceReachableProductDataGroup(), factory, TimeProvider.System);

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

    private const string Route = "/api/local-node/properties";

    [Fact(DisplayName = "Property route: list is empty on a fresh store")]
    public async Task List_Empty_OnFreshStore()
    {
        var doc = await _client.GetFromJsonAsync<JsonElement>(Route);
        Assert.Equal(0, doc.GetProperty("data").GetArrayLength());
    }

    [Fact(DisplayName = "Property route: create → flat Property shape + server-assigned name")]
    public async Task Create_ReturnsFlatShape_WithAssignedName()
    {
        var resp = await _client.PostAsJsonAsync(Route, new
        {
            property_name = "150 Lexington Ct",
            address_line_1 = "150 Lexington Ct",
            city = "Springfield",
            state = "IL",
            postal_code = "62701",
            units = 4,
            status = "Active",
            company = "Acme Holdings",
        });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var prop = doc.GetProperty("data");

        Assert.False(string.IsNullOrWhiteSpace(prop.GetProperty("name").GetString()));
        Assert.Equal("150 Lexington Ct", prop.GetProperty("property_name").GetString());
        Assert.Equal("Springfield", prop.GetProperty("city").GetString());
        Assert.Equal(4, prop.GetProperty("units").GetInt32());
        Assert.Equal("Active", prop.GetProperty("status").GetString());
        Assert.Equal("Acme Holdings", prop.GetProperty("company").GetString());
    }

    [Fact(DisplayName = "Property route: create defaults status to Active when omitted")]
    public async Task Create_DefaultsStatus_ToActive()
    {
        var resp = await _client.PostAsJsonAsync(Route, new { property_name = "Oak Ave" });
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Active", doc.GetProperty("data").GetProperty("status").GetString());
    }

    [Fact(DisplayName = "Property route: create rejects missing property_name (400)")]
    public async Task Create_RejectsMissingPropertyName()
    {
        var resp = await _client.PostAsJsonAsync(Route, new { city = "Nowhere" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact(DisplayName = "Property route: create rejects invalid status (400)")]
    public async Task Create_RejectsInvalidStatus()
    {
        var resp = await _client.PostAsJsonAsync(Route, new { property_name = "x", status = "Demolished" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact(DisplayName = "Property route: create → get → list round-trips over HTTP")]
    public async Task Create_Get_List_RoundTrips()
    {
        var created = await _client.PostAsJsonAsync(Route, new { property_name = "Roundtrip Plaza" });
        var name = (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("data").GetProperty("name").GetString();

        var got = await _client.GetFromJsonAsync<JsonElement>($"{Route}/{name}");
        Assert.Equal("Roundtrip Plaza", got.GetProperty("data").GetProperty("property_name").GetString());

        var list = await _client.GetFromJsonAsync<JsonElement>(Route);
        Assert.Equal(1, list.GetProperty("data").GetArrayLength());
    }

    [Fact(DisplayName = "Property route: get missing → 404")]
    public async Task Get_Missing_Returns404()
    {
        var resp = await _client.GetAsync($"{Route}/PROP-99999");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
