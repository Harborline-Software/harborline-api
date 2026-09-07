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
using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Blocks.Assets.Registry.DependencyInjection;
using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Packs.Dcp;
using Harborline.Api.Foundation.Packs.Export;
using Harborline.Api.Foundation.Packs.Graph;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Audit;
using Harborline.Api.Foundation.Packs.Install.Trust;
using Harborline.Api.Foundation.Packs.Serialization;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Foundation.Packs.Validation;
using Harborline.Api.Foundation.Packs.Verify;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.PackProjection;
using Harborline.Api.LocalNodeHost.Health;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

/// <summary>
/// G1 keystone — the app-layer feature graph end-to-end through the REAL <c>GET /packs/graph</c> route
/// (design note <c>_shared/design/app-layer-feature-graph-2026-07-07.md</c>). Installs + activates TWO apps
/// (a base "core-records" and a "fleet-ops" whose Vehicle type sets <c>parentType</c> into core-records)
/// through the same production install routes + <see cref="PackSeedProjector"/> the host wires, then reads
/// the graph and asserts: two apps, contributions grouped by pillar with provenance, and the CROSS-APP
/// content-reference edge. Same handlers, no test/prod drift.
/// </summary>
public sealed class PackGraphRouteTests : IAsyncLifetime
{
    private static readonly TeamId TeamA = new(Guid.Parse("aaaa0000-0000-0000-0000-0000000136a1"));
    private const string GraphRoute = "/api/local-node/packs/graph";

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private KeyPair _key = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddLogging();
        builder.Services.AddInMemoryAssetTypeSystem();
        _app = builder.Build();

        _key = KeyPair.Generate();
        var signer = new Ed25519Signer(_key);
        var codec = new PackFileCodec();
        var exporter = new PackExporter(
            new PackContentCanonicalizer(), new PackDcpCanonicalizer(),
            new PackValidator(new PackContentPiiScanner()),
            new DcpValidator(DcpCounselRegister.FromEmbeddedResource()), codec, timeProvider: TimeProvider.System);
        var verifier = new PackVerifier(new Ed25519Verifier(), codec);
        var trustStore = new InMemoryPackTrustStore(new[]
        {
            new PackTrustRoot(TrustScope.OwnRoster, _key.PrincipalId, PackComposerRoutes.OwnRosterEpoch, TrustRootStatus.Current),
        });
        var activeTeam = new MutableActiveTeamAccessor(TeamContextFor(TeamA, "Graph Co"));

        var store = new InMemoryPackInstallStore();
        var admission = new PackWorkflowAdmissionAdapter(new WorkflowAdmissionValidator());
        var installer = new PackInstaller(verifier, store, admission, new InMemoryPackInstallAudit(),
            Harborline.Api.LocalNodeHost.Tests.Authorization.TestAuthorization.AllowGate());
        var registry = _app.Services.GetRequiredService<IEntityTypeRegistry>();

        // The G1 read-model + the rebuildable content-edge-index provider — the SAME types the host DI wires.
        // The projector gets the provider so a projection pass warms the index (the note's side-effect); the
        // read-model also self-heals on read, so either path leaves the graph correct.
        var edgeIndex = new InMemoryPackContentEdgeIndexProvider(store);
        var readModel = new PackFeatureGraphReadModel(store, edgeIndex);
        var projector = new PackSeedProjector(
            store, registry, NullLogger<PackSeedProjector>.Instance, templates: null, edgeIndex: edgeIndex, time: TimeProvider.System);

        var authz = TestPackGate.AllowAll();
        _app.Use(async (http, next) =>
        {
            http.Features.Set(DesktopPlaneRequestFeature.Instance);
            await next(http);
        });
        PackComposerRoutes.Map(_app, exporter, verifier, trustStore, signer, activeTeam, authz, TimeProvider.System, NullLogger.Instance);
        PackInstallRoutes.Map(_app, installer, store, trustStore, PackRevocationList.Empty, activeTeam,
            authz, TimeProvider.System, NullLogger.Instance, authorizingPrincipal: _key.PrincipalId.ToBase64Url(),
            projector: projector);
        PackGraphRoutes.Map(_app.MapDeviceReachableProductDataGroup(), readModel, activeTeam);

        await _app.StartAsync();
        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        _client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) };

        // Install + activate the base app FIRST, then the app that builds on it.
        await InstallAndActivateAsync(CoreRecordsBody());
        await InstallAndActivateAsync(FleetOpsBody());
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        _key?.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    [Fact(DisplayName = "GET /packs/graph returns both apps' contributions grouped by pillar, with provenance")]
    public async Task Graph_returns_both_apps_grouped_by_pillar()
    {
        using var doc = await GetJsonAsync(GraphRoute);
        var root = doc.RootElement;

        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("installStateFingerprint").GetString()));
        var apps = root.GetProperty("apps").EnumerateArray().ToList();
        Assert.Equal(2, apps.Count);

        var fleet = Assert.Single(apps, a => a.GetProperty("packKey").GetString() == "fleet-ops");
        Assert.Equal("Active", fleet.GetProperty("lifecycle").GetString());
        Assert.Equal(2, fleet.GetProperty("contributionCount").GetInt32());

        // Records pillar carries the vehicle (with its app-supplied label + provenance); Forms the form.
        var pillars = fleet.GetProperty("pillars").EnumerateArray().ToList();
        var records = Assert.Single(pillars, p => p.GetProperty("pillar").GetString() == "Records");
        var vehicle = Assert.Single(records.GetProperty("nodes").EnumerateArray().ToList(),
            n => n.GetProperty("contentKey").GetString() == "fleet-ops.vehicle");
        Assert.Equal("Vehicle", vehicle.GetProperty("label").GetString());
        Assert.Equal("recordType", vehicle.GetProperty("kindNoun").GetString());
        Assert.Equal("fleet-ops", vehicle.GetProperty("owningPackKey").GetString());
        Assert.Single(pillars, p => p.GetProperty("pillar").GetString() == "Forms");
    }

    [Fact(DisplayName = "GET /packs/graph surfaces the cross-app content-reference edge (Vehicle → Property)")]
    public async Task Graph_surfaces_cross_app_edge()
    {
        using var doc = await GetJsonAsync(GraphRoute);
        var edges = doc.RootElement.GetProperty("edges").EnumerateArray().ToList();

        var edge = Assert.Single(edges, e => e.GetProperty("fromContentKey").GetString() == "fleet-ops.vehicle");
        Assert.Equal("CrossAppContentReference", edge.GetProperty("edgeClass").GetString());
        Assert.Equal("ParentOf", edge.GetProperty("relation").GetString());
        Assert.Equal("core-records.property", edge.GetProperty("toContentKey").GetString());
        Assert.Equal("core-records", edge.GetProperty("toPackKey").GetString());
    }

    [Fact(DisplayName = "GET /packs/graph?app=<key> returns one slice; an unknown app is 404")]
    public async Task Graph_app_filter_returns_one_slice_or_404()
    {
        using var doc = await GetJsonAsync($"{GraphRoute}?app=fleet-ops");
        Assert.Equal("fleet-ops", doc.RootElement.GetProperty("packKey").GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("contributionCount").GetInt32());

        var missing = await _client.GetAsync($"{GraphRoute}?app=does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    private async Task<JsonDocument> GetJsonAsync(string route)
    {
        var resp = await _client.GetAsync(route);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
    }

    private async Task InstallAndActivateAsync(object body)
    {
        var packBytes = await ExportAsync(body);
        var install = await PostBytesAsync(PackInstallRoutes.InstallRoute, packBytes);
        Assert.Equal(HttpStatusCode.OK, install.StatusCode);

        var packKey = body.GetType().GetProperty("key")!.GetValue(body)!.ToString();
        var activate = await _client.PostAsJsonAsync(PackInstallRoutes.ActivateRoute,
            new { packKey, version = "1.0.0" });
        Assert.Equal(HttpStatusCode.OK, activate.StatusCode);
    }

    private async Task<byte[]> ExportAsync(object body)
    {
        var resp = await _client.PostAsJsonAsync(PackComposerRoutes.ExportRoute, body);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return await resp.Content.ReadAsByteArrayAsync();
    }

    private Task<HttpResponseMessage> PostBytesAsync(string route, byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return _client.PostAsync(route, content);
    }

    private static object CoreRecordsBody() => new
    {
        key = "core-records",
        version = "1.0.0",
        name = "Core Records",
        description = "Base record types other apps build on.",
        scopeTier = "Horizontal",
        contents = new object[]
        {
            AssetType("core-records.property", "Property", new[] { "Container" }),
        },
        dependencies = Array.Empty<object>(),
        capabilityRequirements = Array.Empty<string>(),
    };

    private static object FleetOpsBody() => new
    {
        key = "fleet-ops",
        version = "1.0.0",
        name = "Fleet Ops",
        description = "Adds a Vehicle record type (built on Core Records' Property) + a walkaround form.",
        scopeTier = "Horizontal",
        contents = new object[]
        {
            // parentType → core-records.property: the CROSS-APP content-reference edge (class 3).
            AssetType("fleet-ops.vehicle", "Vehicle", new[] { "Movable", "Maintainable" },
                parentType: "core-records.property"),
            new
            {
                key = "fleet-ops.walkaround",
                kind = "FormDefinition",
                version = "1.0.0",
                content = new { title = "Daily walkaround" },
            },
        },
        dependencies = Array.Empty<object>(),
        capabilityRequirements = Array.Empty<string>(),
    };

    private static object AssetType(string id, string displayName, string[] traits, string? parentType = null)
        => new
        {
            key = id,
            kind = "AssetTypeDefinition",
            version = "1.0.0",
            content = parentType is null
                ? (object)new { id, displayName, traits }
                : new { id, displayName, traits, parentType },
        };

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
