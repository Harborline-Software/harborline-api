using System.Net;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Blobs;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Trust;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Health;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

/// <summary>T3-3 — the pack-driven Harborline App navigation route through its real HTTP mapping.</summary>
public sealed class PackNavigationRouteTests
{
    private static readonly TenantId Tenant = new("aaaaaaaa-0000-0000-0000-000000002045");
    private static readonly PrincipalId Signer = PrincipalId.FromBytes(new byte[PrincipalId.LengthInBytes]);

    [Fact(DisplayName = "GET navigation returns the migration fallback signal when no Active pack contributes")]
    public async Task No_active_navigation_returns_not_configured()
    {
        using var response = await GetAsync(new FakeStore());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(json.RootElement.GetProperty("configured").GetBoolean());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("pack").ValueKind);
    }

    [Fact(DisplayName = "GET navigation composes Active contributions in stable pack-key order")]
    public async Task Active_contributions_are_stable_independent_of_install_order()
    {
        var activatedZebraThenAlpha = new FakeStore(
            Pack("zebra.pack", PackLifecycleState.Active, Nav("z-nav", Workspace("zebra", "workspaces.operations", "assets"))),
            Pack("alpha.pack", PackLifecycleState.Active, Nav("a-nav", Workspace("alpha", "workspaces.front-desk", "inbox"))));
        var activatedAlphaThenZebra = new FakeStore(
            Pack("alpha.pack", PackLifecycleState.Active, Nav("a-nav", Workspace("alpha", "workspaces.front-desk", "inbox"))),
            Pack("zebra.pack", PackLifecycleState.Active, Nav("z-nav", Workspace("zebra", "workspaces.operations", "assets"))));

        using var firstResponse = await GetAsync(activatedZebraThenAlpha);
        using var secondResponse = await GetAsync(activatedAlphaThenZebra);

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        var firstBytes = await firstResponse.Content.ReadAsByteArrayAsync();
        var secondBytes = await secondResponse.Content.ReadAsByteArrayAsync();
        Assert.Equal(firstBytes, secondBytes);

        using var json = JsonDocument.Parse(firstBytes);
        Assert.True(json.RootElement.GetProperty("configured").GetBoolean());
        var pack = json.RootElement.GetProperty("pack");
        Assert.Equal(PackNavigationRoutes.ComposedPackId, pack.GetProperty("packId").GetString());
        Assert.Equal(
            new[] { "alpha", "zebra" },
            pack.GetProperty("seedWorkspaces").EnumerateArray()
                .Select(w => w.GetProperty("id").GetString())
                .ToArray());
    }

    [Fact(DisplayName = "GET navigation ignores Draft and Superseded contributions")]
    public async Task Only_active_contributions_are_visible()
    {
        var store = new FakeStore(
            Pack("draft.pack", PackLifecycleState.Draft, Nav("live-nav", Workspace("draft", "workspaces.front-desk", "inbox"))),
            Pack("old.pack", PackLifecycleState.Superseded, Nav("live-nav", Workspace("old", "workspaces.money", "invoices"))),
            Pack("live.pack", PackLifecycleState.Active, Nav("live-nav", Workspace("live", "workspaces.operations", "assets"))));

        using var response = await GetAsync(store);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var workspaces = json.RootElement.GetProperty("pack").GetProperty("seedWorkspaces");
        Assert.Equal("live", Assert.Single(workspaces.EnumerateArray().ToArray()).GetProperty("id").GetString());
    }

    [Fact(DisplayName = "GET navigation honors recorded same-content-key ownership")]
    public async Task Recorded_content_owner_is_the_only_contributor()
    {
        var store = new FakeStore(
            Pack("zebra.pack", PackLifecycleState.Active, Nav("shared-nav", Workspace("zebra", "workspaces.operations", "assets"))),
            Pack("alpha.pack", PackLifecycleState.Active, Nav("shared-nav", Workspace("alpha", "workspaces.front-desk", "inbox"))));
        store.RecordKeyOwnership(Tenant, "shared-nav", "alpha.pack");

        using var response = await GetAsync(store);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var workspace = Assert.Single(json.RootElement.GetProperty("pack").GetProperty("seedWorkspaces")
            .EnumerateArray().ToArray());
        Assert.Equal("alpha", workspace.GetProperty("id").GetString());
    }

    [Fact(DisplayName = "GET navigation refuses an unresolved same-content-key collision")]
    public async Task Unresolved_content_key_collision_is_fail_closed()
    {
        var store = new FakeStore(
            Pack("zebra.pack", PackLifecycleState.Active,
                Nav("shared-nav", Workspace("zebra", "workspaces.operations", "assets"))),
            Pack("alpha.pack", PackLifecycleState.Active,
                Nav("shared-nav", Workspace("alpha", "workspaces.front-desk", "inbox"))));

        using var response = await GetAsync(store);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("pack.nav.content_key_conflict", json.RootElement.GetProperty("code").GetString());
    }

    [Fact(DisplayName = "GET navigation refuses duplicate workspace ids with a localizable conflict code")]
    public async Task Duplicate_workspace_refuses_without_mutating_activation()
    {
        var alpha = Pack("alpha.pack", PackLifecycleState.Active,
            Nav("alpha-nav", Workspace("shared", "workspaces.front-desk", "inbox")));
        var zebra = Pack("zebra.pack", PackLifecycleState.Active,
            Nav("zebra-nav", Workspace("shared", "workspaces.operations", "assets")));
        var store = new FakeStore(alpha, zebra);

        using var response = await GetAsync(store);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("pack.nav.duplicate_workspace", json.RootElement.GetProperty("code").GetString());
        Assert.Equal(PackLifecycleState.Active, store.GetActive(Tenant, "alpha.pack")!.Lifecycle);
        Assert.Equal(PackLifecycleState.Active, store.GetActive(Tenant, "zebra.pack")!.Lifecycle);
    }

    [Fact(DisplayName = "GET navigation refuses malformed legacy placeholder content with a stable code")]
    public async Task Malformed_navigation_is_an_honest_refusal()
    {
        var store = new FakeStore(Pack(
            "legacy.pack",
            PackLifecycleState.Active,
            Seed("legacy-nav", """{"title":"Welcome","sections":["home"]}""")));

        using var response = await GetAsync(store);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("pack.nav.malformed", json.RootElement.GetProperty("code").GetString());
    }

    [Fact(DisplayName = "GET navigation refuses a contribution beyond the workspace bound")]
    public async Task Workspace_bound_is_fail_closed()
    {
        var workspaces = Enumerable.Range(0, 33)
            .Select(i => Workspace($"workspace-{i}", "workspaces.operations", "assets"))
            .ToArray();
        var store = new FakeStore(Pack("wide.pack", PackLifecycleState.Active, Nav("wide-nav", workspaces)));

        using var response = await GetAsync(store);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("pack.nav.bounds_exceeded", json.RootElement.GetProperty("code").GetString());
    }

    [Fact(DisplayName = "GET navigation composes mode, create, honest count, panels and document spine without undeclared panels")]
    public async Task Composed_dto_carries_declared_chrome_and_omits_undeclared_panels()
    {
        var json =
            """
            {
              "seedWorkspaces": [{
                "id": "operations",
                "labelKey": "workspaces.operations",
                "destinationQueryRef": "queries.assets",
                "countQueryRef": "queries.assets",
                "createActions": [{
                  "id": "create-asset",
                  "verbKey": "actions.asset.create",
                  "icon": "plus",
                  "binding": "assets.create",
                  "shortcut": "mod+n",
                  "permittedRoles": ["tax.roles/maintainer"]
                }],
                "documentSpine": [{
                  "id": "assets",
                  "labelKey": "documents.assets",
                  "binding": "documents.assets"
                }]
              }, {
                "id": "definitions",
                "labelKey": "workspaces.definitions",
                "documentSpine": [{
                  "id": "definitions",
                  "labelKey": "documents.definitions",
                  "binding": "documents.definitions"
                }]
              }],
              "modeSwitch": { "modes": [
                { "id": "operate", "labelKey": "modes.operate", "workspaceIds": ["operations"] },
                { "id": "configure", "labelKey": "modes.configure", "workspaceIds": ["definitions"] }
              ]},
              "panelSet": [{
                "id": "documents",
                "binding": "panels.documents.toggle",
                "shortcut": "mod+shift+d",
                "defaultWidth": 420,
                "minimumHeight": 220,
                "defaultOpen": true
              }]
            }
            """;
        var store = new FakeStore(Pack("chrome.pack", PackLifecycleState.Active, Seed("chrome", json)));

        using var response = await GetAsync(store);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var pack = document.RootElement.GetProperty("pack");
        Assert.Equal("operate", pack.GetProperty("modeSwitch").GetProperty("modes")[0].GetProperty("id").GetString());
        var operations = pack.GetProperty("seedWorkspaces")[0];
        Assert.Equal("queries.assets", operations.GetProperty("countQueryRef").GetString());
        Assert.Equal("mod+n", operations.GetProperty("createActions")[0].GetProperty("shortcut").GetString());
        Assert.Equal("assets", operations.GetProperty("documentSpine")[0].GetProperty("id").GetString());
        var panel = Assert.Single(pack.GetProperty("panelSet").EnumerateArray().ToArray());
        Assert.Equal("documents", panel.GetProperty("id").GetString());
        Assert.Equal("Library", panel.GetProperty("bodyTemplate").GetString());
        Assert.DoesNotContain(pack.GetProperty("panelSet").EnumerateArray(),
            candidate => candidate.GetProperty("id").GetString() == "notifications");
    }

    [Fact(DisplayName = "GET navigation applies the workspace bound to the composed total")]
    public async Task Composed_workspace_bound_is_fail_closed()
    {
        var alphaWorkspaces = Enumerable.Range(0, 20)
            .Select(i => Workspace($"alpha-{i}", "workspaces.front-desk", $"alpha-item-{i}"))
            .ToArray();
        var zebraWorkspaces = Enumerable.Range(0, 20)
            .Select(i => Workspace($"zebra-{i}", "workspaces.operations", $"zebra-item-{i}"))
            .ToArray();
        var store = new FakeStore(
            Pack("alpha.pack", PackLifecycleState.Active, Nav("alpha-nav", alphaWorkspaces)),
            Pack("zebra.pack", PackLifecycleState.Active, Nav("zebra-nav", zebraWorkspaces)));

        using var response = await GetAsync(store);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("pack.nav.bounds_exceeded", json.RootElement.GetProperty("code").GetString());
    }

    private static async Task<HttpResponseMessage> GetAsync(FakeStore store)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("HARBORLINE_PACK_NAVIGATION_TEST_URL") ?? "http://127.0.0.1:0");
        var app = builder.Build();
        var team = new TeamId(Guid.Parse(Tenant.Value));
        var activeTeam = new FixedActiveTeamAccessor(new TeamContext(
            team, "Navigation Test", new ServiceCollection().BuildServiceProvider(), TimeProvider.System));
        app.Use(async (http, next) =>
        {
            http.Features.Set(DesktopPlaneRequestFeature.Instance);
            await next(http);
        });
        PackNavigationRoutes.Map(
            app.MapDeviceReachableProductDataGroup(),
            store,
            activeTeam,
            NullLogger.Instance);

        await app.StartAsync();
        try
        {
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
            using var client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) };
            return await client.GetAsync(PackNavigationRoutes.NavigationRoute);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private static PackSeedItem Nav(string key, params object[] workspaces)
        => Seed(key, JsonSerializer.Serialize(new { seedWorkspaces = workspaces }));

    private static object Workspace(string id, string labelKey, string itemId) => new
    {
        id,
        labelKey,
        groups = new[]
        {
            new { id = "primary", labelKey = "workspaceGroups.front-desk.today", itemIds = new[] { itemId }, items = new[] { new { id = itemId, labelKey = "navigation." + itemId } } },
        },
    };

    private static PackSeedItem Seed(string key, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        return new PackSeedItem(key, PackContentKind.NavWorkspaceConfig, "1.0.0", json, Cid.FromBytes(bytes));
    }

    private static InstalledPack Pack(
        string key,
        PackLifecycleState lifecycle,
        params PackSeedItem[] items)
        => new(
            key,
            "1.0.0",
            PackScopeTier.Horizontal,
            lifecycle,
            items,
            new Dictionary<string, int>(),
            DateTimeOffset.UnixEpoch,
            Signer,
            1,
            TrustScope.OwnRoster,
            Array.Empty<PackDependencyRef>());

    private sealed class FakeStore(params InstalledPack[] packs) : IPackInstallStore
    {
        private readonly List<InstalledPack> _packs = packs.ToList();
        private readonly Dictionary<string, string> _ownership = new(StringComparer.Ordinal);

        public InstalledPack? GetActive(TenantId tenant, string packKey)
            => _packs.FirstOrDefault(p => p.PackKey == packKey && p.Lifecycle == PackLifecycleState.Active);

        public InstalledPack? GetVersion(TenantId tenant, string packKey, string version)
            => _packs.FirstOrDefault(p => p.PackKey == packKey && p.Version == version);

        public IReadOnlyList<InstalledPack> ListInstalled(TenantId tenant) => _packs;
        public PackInstallWatermark? GetWatermark(TenantId tenant, string packKey) => null;
        public IReadOnlyList<PackTenantOverride> GetOverrides(TenantId tenant, string packKey) => [];
        public IReadOnlyDictionary<string, string> GetKeyOwnership(TenantId tenant) => _ownership;
        public void SaveOverride(TenantId tenant, string packKey, PackTenantOverride tenantOverride)
            => throw new NotSupportedException();
        public void Commit(PackInstallTransaction transaction) => throw new NotSupportedException();
        public void Activate(TenantId tenant, string packKey, string version) => throw new NotSupportedException();
        public void Deactivate(TenantId tenant, string packKey, string version) => throw new NotSupportedException();
        public void RecordKeyOwnership(TenantId tenant, string contentKey, string owningPackKey)
            => _ownership[contentKey] = owningPackKey;
    }

    private sealed class FixedActiveTeamAccessor(TeamContext active) : IActiveTeamAccessor
    {
        public TeamContext? Active { get; } = active;
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged;
        private void KeepEvent() => ActiveChanged?.Invoke(this, new ActiveTeamChangedEventArgs(null, null));
    }
}
