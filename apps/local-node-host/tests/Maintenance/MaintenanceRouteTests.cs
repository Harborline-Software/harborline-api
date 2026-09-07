using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Harborline.Api.LocalNodeHost.Data.Maintenance;
using Harborline.Api.LocalNodeHost.Health;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Maintenance;

/// <summary>
/// ADR 0115 D8 Stage 2 — route-level tests for the node-local maintenance API.
/// </summary>
/// <remarks>
/// <para>
/// These tests host the SAME route handlers <see cref="Harborline.Api.LocalNodeHost.Health.HostedMaintenanceApiEndpoint"/>
/// registers, on a real in-process Kestrel listener bound to an ephemeral
/// loopback port, backed by a keyed in-memory-file SQLite store, and drive them
/// with a real <see cref="HttpClient"/>. They prove the wire contract end-to-end:
/// list / get / create / update, the flat <c>MaintenanceTicket</c> JSON shape,
/// server-assigned names, validation (400), and not-found (404).
/// </para>
/// <para>
/// The route bodies are duplicated here intentionally minimally? No — to avoid
/// drift, the test invokes the production registration helper
/// <see cref="MaintenanceRoutes.Map"/> that the hosted service also calls, so a
/// single source of truth maps the routes. (The hosted service is a thin
/// IHostedService wrapper around that mapping.)
/// </para>
/// </remarks>
public sealed class MaintenanceRouteTests : IAsyncLifetime
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

        // A real on-disk temp SQLite file: an in-memory (Mode=Memory) DB is
        // per-connection, so the AddDbContextFactory pattern (a fresh connection
        // per request) would not see the schema migrated up front. A file-backed
        // store persists across the factory's per-request connections. (SC-1
        // encryption is covered separately by MaintenanceStoreTests; this test
        // focuses on route behaviour, so a plain unencrypted file is sufficient.)
        _dir = Path.Combine(Path.GetTempPath(), "harborline-maint-routes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var connectionString = $"Data Source={Path.Combine(_dir, "maint.db")};Pooling=False";
        builder.Services.AddDbContextFactory<NodeLocalMaintenanceDbContext>(opt =>
            opt.UseSqlite(connectionString));

        _app = builder.Build();

        // Migrate the schema once up front.
        var factory = _app.Services.GetRequiredService<IDbContextFactory<NodeLocalMaintenanceDbContext>>();
        await using (var ctx = await factory.CreateDbContextAsync())
        {
            await ctx.Database.MigrateAsync();
        }

        // Map the SAME production routes, closing over the factory (mirrors the
        // production HostedMaintenanceApiEndpoint wiring — no [FromServices]).
        _app.Use(async (http, next) =>
        {
            http.Features.Set(DesktopPlaneRequestFeature.Instance);
            await next(http);
        });
        MaintenanceRoutes.Map(_app.MapSelectedSessionProductGroup(), factory, TimeProvider.System);

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

    private const string Route = "/api/local-node/maintenance";

    [Fact(DisplayName = "Route: list is empty on a fresh store")]
    public async Task List_Empty_OnFreshStore()
    {
        var doc = await _client.GetFromJsonAsync<JsonElement>(Route);
        Assert.Equal(0, doc.GetProperty("data").GetArrayLength());
    }

    [Fact(DisplayName = "Route: create → flat MaintenanceTicket shape + server-assigned name")]
    public async Task Create_ReturnsFlatShape_WithAssignedName()
    {
        var resp = await _client.PostAsJsonAsync(Route, new
        {
            Subject = "Broken window",
            Property = "PROP-7",
            Priority = "High",
            Description = "Glass cracked in lobby",
        });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var ticket = doc.GetProperty("data");

        Assert.False(string.IsNullOrWhiteSpace(ticket.GetProperty("name").GetString()));
        Assert.Equal("Broken window", ticket.GetProperty("subject").GetString());
        Assert.Equal("PROP-7", ticket.GetProperty("property").GetString());
        Assert.Equal("Open", ticket.GetProperty("status").GetString());
        Assert.Equal("High", ticket.GetProperty("priority").GetString());
        // Flat-shape fields present (frontend MaintenanceTicket contract).
        Assert.True(ticket.TryGetProperty("assigned_to", out _));
        Assert.True(ticket.TryGetProperty("cost", out _));
    }

    [Fact(DisplayName = "Route: create defaults priority to Medium when omitted")]
    public async Task Create_DefaultsPriority_ToMedium()
    {
        var resp = await _client.PostAsJsonAsync(Route, new { Subject = "Filter swap", Property = "P1" });
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Medium", doc.GetProperty("data").GetProperty("priority").GetString());
    }

    [Fact(DisplayName = "Route: create rejects missing subject (400)")]
    public async Task Create_RejectsMissingSubject()
    {
        var resp = await _client.PostAsJsonAsync(Route, new { Property = "P1", Priority = "Low" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact(DisplayName = "Route: create rejects invalid priority (400)")]
    public async Task Create_RejectsInvalidPriority()
    {
        var resp = await _client.PostAsJsonAsync(Route, new { Subject = "x", Priority = "Catastrophic" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact(DisplayName = "Route: create → get → patch round-trips over HTTP")]
    public async Task Create_Get_Patch_RoundTrips()
    {
        var created = await _client.PostAsJsonAsync(Route, new { Subject = "Roundtrip", Property = "P2" });
        var name = (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("data").GetProperty("name").GetString();

        // GET one.
        var got = await _client.GetFromJsonAsync<JsonElement>($"{Route}/{name}");
        Assert.Equal("Roundtrip", got.GetProperty("subject").GetString());

        // PATCH status + cost.
        var patch = new HttpRequestMessage(HttpMethod.Patch, $"{Route}/{name}")
        {
            Content = JsonContent.Create(new { Status = "Resolved", Cost = 42.0m, Resolution = "Fixed" }),
        };
        var patched = await _client.SendAsync(patch);
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        var patchedTicket = (await patched.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal("Resolved", patchedTicket.GetProperty("status").GetString());
        Assert.Equal(42.0m, patchedTicket.GetProperty("cost").GetDecimal());
    }

    [Fact(DisplayName = "Route: patch invalid status (400)")]
    public async Task Patch_RejectsInvalidStatus()
    {
        var created = await _client.PostAsJsonAsync(Route, new { Subject = "y" });
        var name = (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("data").GetProperty("name").GetString();

        var patch = new HttpRequestMessage(HttpMethod.Patch, $"{Route}/{name}")
        {
            Content = JsonContent.Create(new { Status = "Vaporized" }),
        };
        var resp = await _client.SendAsync(patch);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact(DisplayName = "Route: get missing → 404")]
    public async Task Get_Missing_Returns404()
    {
        var resp = await _client.GetAsync($"{Route}/TKT-99999");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact(DisplayName = "Route: patch missing → 404")]
    public async Task Patch_Missing_Returns404()
    {
        var patch = new HttpRequestMessage(HttpMethod.Patch, $"{Route}/TKT-99999")
        {
            Content = JsonContent.Create(new { Status = "Closed" }),
        };
        var resp = await _client.SendAsync(patch);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
