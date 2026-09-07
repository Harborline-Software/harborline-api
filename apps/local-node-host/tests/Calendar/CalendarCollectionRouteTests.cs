using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Harborline.Api.Blocks.Calendar.DependencyInjection;
using Harborline.Api.Blocks.Calendar.Services;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Health;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Calendar;

/// <summary>
/// Calendar-productization #149 slice C1 route-level tests for the node-local owned-calendar collection
/// API (<see cref="CalendarCollectionRoutes"/>). Hosts the SAME production route handlers over a real
/// in-process Kestrel listener (mirrors <c>HostedCalendarCollectionApiEndpoint</c>, no test/prod wire
/// drift) and drives them with a real <see cref="HttpClient"/>. Proves: list + create, kind + resource
/// handling, input validation, per-route caller-auth (fail-closed 401 when enforced), and cross-tenant
/// isolation.
/// </summary>
public sealed class CalendarCollectionRouteTests : IAsyncLifetime
{
    private static readonly TeamId TeamA = new(Guid.Parse("aaaa0000-0000-0000-0000-0000000000a1"));
    private static readonly TeamId TeamB = new(Guid.Parse("bbbb0000-0000-0000-0000-0000000000b1"));

    private const string Base = "/api/local-node/calendar/calendars";

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private MutableActiveTeamAccessor _activeTeam = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddBlocksCalendar(); // in-memory ICalendarStore

        _app = builder.Build();
        _activeTeam = new MutableActiveTeamAccessor(TeamContextFor(TeamA, "Team A"));

        // Un-enforced caller-auth (dev mode) for the functional tests — the enforced 401 case builds its
        // own app inline (see CallerAuth_Enforced_Rejects_Tokenless).
        _app.Use(async (http, next) =>
        {
            http.Features.Set(DesktopPlaneRequestFeature.Instance);
            await next(http);
        });
        CalendarCollectionRoutes.Map(
            _app.MapDeviceReachableProductDataGroup(),
            _app.Services.GetRequiredService<ICalendarStore>(),
            _activeTeam,
            new NodeCallerSessionToken(configuredToken: null));

        await _app.StartAsync();
        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        _client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) };
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    // ── list + create ───────────────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "list: an empty tenant returns no calendars")]
    public async Task List_Empty()
    {
        var doc = await _client.GetFromJsonAsync<JsonElement>(Base);
        Assert.Equal(0, doc.GetProperty("data").GetArrayLength());
    }

    [Fact(DisplayName = "create: POST a personal calendar returns 201 and it lists")]
    public async Task Create_Personal()
    {
        var resp = await _client.PostAsJsonAsync(Base, new { name = "My work", kind = "personal" });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var created = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("My work", created.GetProperty("name").GetString());
        Assert.Equal("personal", created.GetProperty("kind").GetString());
        Assert.False(created.GetProperty("isDefault").GetBoolean());

        var list = await _client.GetFromJsonAsync<JsonElement>(Base);
        Assert.Equal(1, list.GetProperty("data").GetArrayLength());
    }

    [Fact(DisplayName = "create: a resource calendar carries its resource ref on the wire")]
    public async Task Create_Resource_CarriesRef()
    {
        var resp = await _client.PostAsJsonAsync(Base,
            new { name = "Exam Room 3", kind = "resource", resource = "asset:room-7" });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var created = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("resource", created.GetProperty("kind").GetString());
        Assert.Equal("asset:room-7", created.GetProperty("resource").GetString());
    }

    [Fact(DisplayName = "create: kind defaults to personal when omitted")]
    public async Task Create_DefaultsToPersonal()
    {
        var resp = await _client.PostAsJsonAsync(Base, new { name = "No kind" });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var created = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("personal", created.GetProperty("kind").GetString());
    }

    // ── validation ──────────────────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "create: a missing name is 400")]
    public async Task Create_MissingName_400()
    {
        var resp = await _client.PostAsJsonAsync(Base, new { kind = "personal" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact(DisplayName = "create: an unknown kind is 400")]
    public async Task Create_UnknownKind_400()
    {
        var resp = await _client.PostAsJsonAsync(Base, new { name = "X", kind = "nonsense" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact(DisplayName = "create: a resource calendar without a resource ref is 400 (entity invariant)")]
    public async Task Create_ResourceWithoutRef_400()
    {
        var resp = await _client.PostAsJsonAsync(Base, new { name = "Room", kind = "resource" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ── cross-tenant isolation ────────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "isolation: a calendar created under team A is not visible to team B")]
    public async Task CrossTenant_Isolated()
    {
        await _client.PostAsJsonAsync(Base, new { name = "Team-A cal", kind = "personal" });

        _activeTeam.Active = TeamContextFor(TeamB, "Team B");
        var docB = await _client.GetFromJsonAsync<JsonElement>(Base);
        Assert.Equal(0, docB.GetProperty("data").GetArrayLength());

        _activeTeam.Active = TeamContextFor(TeamA, "Team A");
        var docA = await _client.GetFromJsonAsync<JsonElement>(Base);
        Assert.Equal(1, docA.GetProperty("data").GetArrayLength());
    }

    // ── per-route caller-auth (fail-closed) ───────────────────────────────────────────────────────

    [Fact(DisplayName = "caller-auth: an ENFORCED token rejects a tokenless GET and POST with 401")]
    public async Task CallerAuth_Enforced_Rejects_Tokenless()
    {
        const string token = "secret-per-boot-token";

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddBlocksCalendar();

        var app = builder.Build();
        try
        {
            app.Use(async (http, next) =>
            {
                http.Features.Set(DesktopPlaneRequestFeature.Instance);
                await next(http);
            });
            CalendarCollectionRoutes.Map(
                app.MapDeviceReachableProductDataGroup(),
                app.Services.GetRequiredService<ICalendarStore>(),
                new MutableActiveTeamAccessor(TeamContextFor(TeamA, "Team A")),
                new NodeCallerSessionToken(configuredToken: token));

            await app.StartAsync();
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
            using var client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) };

            // No Authorization header → fail-closed 401 on both the read and the write.
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Base)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized,
                (await client.PostAsJsonAsync(Base, new { name = "X", kind = "personal" })).StatusCode);

            // With the correct bearer, the write succeeds.
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var ok = await client.PostAsJsonAsync(Base, new { name = "Authed", kind = "personal" });
            Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────────────

    private static TeamContext TeamContextFor(TeamId teamId, string name)
        => new(teamId, name, new ServiceCollection().BuildServiceProvider(), TimeProvider.System);

    private sealed class MutableActiveTeamAccessor : IActiveTeamAccessor
    {
        public MutableActiveTeamAccessor(TeamContext? active) => Active = active;
        public TeamContext? Active { get; set; }
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged;
        private void _keep() => ActiveChanged?.Invoke(this, new ActiveTeamChangedEventArgs(null, null));
    }
}
