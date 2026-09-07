using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Harborline.Api.Foundation.DataExchangeDefinitions;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Health;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.DataExchangeDefinitions;

/// <summary>
/// HTTP-contract coverage for the read-only report definition route family (ticket 087), mapped
/// onto the PRODUCTION desktop-plane-only group so the fence is exercised, not simulated: requests
/// carry the desktop-plane feature only when they do not opt out via the test header below.
/// </summary>
public sealed class DataExchangeDefinitionRouteTests : IAsyncLifetime
{
    private const string WebPlaneHeader = "X-Test-Web-Plane";

    private static readonly TeamId TeamA = new(Guid.Parse("aaaa0000-0000-0000-0000-00000000aa01"));
    private static readonly TeamId TeamB = new(Guid.Parse("bbbb0000-0000-0000-0000-00000000bb02"));

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private MutableActiveTeamAccessor _activeTeam = null!;
    private InMemoryDataExchangeDefinitionRegistry _registry = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _app = builder.Build();

        _activeTeam = new MutableActiveTeamAccessor(Context(TeamA));
        _registry = new InMemoryDataExchangeDefinitionRegistry(new AcceptAllDescriptorRegistry());
        _app.Use(async (http, next) =>
        {
            if (!http.Request.Headers.ContainsKey(WebPlaneHeader))
            {
                http.Features.Set(DesktopPlaneRequestFeature.Instance);
            }

            await next(http);
        });
        DataExchangeDefinitionRoutes.Map(_app.MapDesktopPlaneOnlyGroup(), _registry, _activeTeam);

        await _app.StartAsync();
        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        _client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) };

        var tenantA = Tenant(TeamA);
        await _registry.RegisterAsync(Definition(tenantA, "trial", "1.9.0", "Trial balance"));
        await _registry.RegisterAsync(Definition(tenantA, "trial", "1.10.0", "Trial balance"));
        await _registry.RegisterAsync(Definition(tenantA, "aging", "3.0.0", "AR aging"));
        await _registry.RegisterAsync(Definition(Tenant(TeamB), "foreign", "9.9.9", "Foreign"));
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task List_returns_one_head_per_key_for_the_active_tenant_only()
    {
        var rows = await _client.GetFromJsonAsync<JsonElement>(DataExchangeDefinitionRoutes.RouteBase);

        Assert.Equal(2, rows.GetArrayLength());
        Assert.Equal("aging", rows[0].GetProperty("key").GetString());
        Assert.Equal("3.0.0", rows[0].GetProperty("version").GetString());
        Assert.Equal("trial", rows[1].GetProperty("key").GetString());
        // 1.10.0 above 1.9.0 proves the semver rule reached the wire (ordinal would invert them).
        Assert.Equal("1.10.0", rows[1].GetProperty("version").GetString());
        Assert.Equal("Tenant", rows[1].GetProperty("cascadeLayer").GetString());
    }

    [Fact]
    public async Task Detail_serves_the_head_with_envelope_metadata_and_hides_foreign_tenants()
    {
        var detail = await _client.GetFromJsonAsync<JsonElement>($"{DataExchangeDefinitionRoutes.RouteBase}/trial");

        Assert.Equal("1.10.0", detail.GetProperty("version").GetString());
        Assert.Equal("import", detail.GetProperty("exchangeKind").GetString());
        Assert.Equal(1, detail.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(JsonValueKind.Object, detail.GetProperty("settings").ValueKind);
        Assert.True(detail.TryGetProperty("provenance", out _));

        // INV-S1: a foreign tenant's key and an unknown key are indistinguishable.
        Assert.Equal(HttpStatusCode.NotFound,
            (await _client.GetAsync($"{DataExchangeDefinitionRoutes.RouteBase}/foreign")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await _client.GetAsync($"{DataExchangeDefinitionRoutes.RouteBase}/unknown")).StatusCode);
    }

    [Fact]
    public async Task Every_404_carries_a_machine_code_and_no_English()
    {
        // Ticket 092: the wire never carries English — the client localizes the code. Before this
        // pin the three registry families answered with a prose sentence while scheduling answered
        // with a code, and nothing failed, because no test read the body at all.
        var unknownKey = await ReadBodyAsync($"{DataExchangeDefinitionRoutes.RouteBase}/unknown");
        var unknownVersions = await ReadBodyAsync($"{DataExchangeDefinitionRoutes.RouteBase}/unknown/versions");
        var unknownVersion = await ReadBodyAsync($"{DataExchangeDefinitionRoutes.RouteBase}/trial/versions/0.0.0-absent");

        Assert.Equal("data_exchange_definition.not_found", unknownKey.GetProperty("code").GetString());
        Assert.Equal("data_exchange_definition.not_found", unknownVersions.GetProperty("code").GetString());
        Assert.Equal("data_exchange_definition.version_not_found", unknownVersion.GetProperty("code").GetString());
        foreach (var body in new[] { unknownKey, unknownVersions, unknownVersion })
        {
            Assert.False(body.TryGetProperty("error", out _),
                "the retired 'error' field is still on the wire — both fields cannot coexist or clients will decode whichever they were written against");
            Assert.Single(body.EnumerateObject());
        }
    }

    /// <summary>Reads a refusal body as JSON.</summary>
    /// <param name="route">The route to call.</param>
    /// <returns>The parsed response body.</returns>
    private async Task<JsonElement> ReadBodyAsync(string route)
    {
        var response = await _client.GetAsync(route);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    [Fact]
    public async Task Versions_lists_newest_first_with_the_ordering_rule_named()
    {
        var history = await _client.GetFromJsonAsync<JsonElement>(
            $"{DataExchangeDefinitionRoutes.RouteBase}/trial/versions");

        Assert.Equal("semver", history.GetProperty("ordering").GetString());
        var versions = history.GetProperty("versions");
        Assert.Equal(2, versions.GetArrayLength());
        Assert.Equal("1.10.0", versions[0].GetProperty("version").GetString());
        Assert.Equal("1.9.0", versions[1].GetProperty("version").GetString());
    }

    [Fact]
    public async Task One_version_is_an_exact_pinned_read_with_404_on_absent_tuples()
    {
        var revision = await _client.GetFromJsonAsync<JsonElement>(
            $"{DataExchangeDefinitionRoutes.RouteBase}/trial/versions/1.9.0");

        Assert.Equal("1.9.0", revision.GetProperty("version").GetString());
        Assert.Equal(HttpStatusCode.NotFound,
            (await _client.GetAsync($"{DataExchangeDefinitionRoutes.RouteBase}/trial/versions/0.0.1")).StatusCode);
    }

    [Fact]
    public async Task Switching_the_active_team_switches_the_visible_inventory()
    {
        _activeTeam.Active = Context(TeamB);

        var rows = await _client.GetFromJsonAsync<JsonElement>(DataExchangeDefinitionRoutes.RouteBase);

        Assert.Equal(1, rows.GetArrayLength());
        Assert.Equal("foreign", rows[0].GetProperty("key").GetString());

        _activeTeam.Active = Context(TeamA);
    }

    [Fact]
    public async Task Web_plane_requests_are_refused_by_the_fence_with_the_stable_code()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, DataExchangeDefinitionRoutes.RouteBase);
        request.Headers.Add(WebPlaneHeader, "1");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("web-plane.route.unavailable", body.GetProperty("code").GetString());
    }

    private static string Tenant(TeamId team) => ActiveTeamTenantContext.ProjectTenantId(team).Value;

    private static TeamContext Context(TeamId id) =>
        new(id, id.Value.ToString("D"), new ServiceCollection().BuildServiceProvider(), TimeProvider.System);

    private static DataExchangeDefinition Definition(string tenant, string key, string version, string title)
    {
        using var parameters = JsonDocument.Parse("{\"chartId\":\"chart-7\"}");
        return new DataExchangeDefinition
        {
            Tenant = tenant,
            Key = key,
            Version = version,
            SchemaVersion = 1,
            ExchangeKind = "import",
            Title = title,
            Settings = parameters.RootElement.Clone(),
        };
    }

    private sealed class AcceptAllDescriptorRegistry : IDataExchangeDefinitionDescriptorRegistry
    {
        public ValueTask AdmitAsync(DataExchangeDefinition definition, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }

    private sealed class MutableActiveTeamAccessor : IActiveTeamAccessor
    {
        public MutableActiveTeamAccessor(TeamContext? active) => Active = active;
        public TeamContext? Active { get; set; }
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged;
        private void _keep() => ActiveChanged?.Invoke(this, new ActiveTeamChangedEventArgs(null, null));
    }
}
