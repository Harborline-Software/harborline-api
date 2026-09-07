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
/// G2 end-to-end through the REAL compose → install → graph routes (app-layer design note §6.3): a pack that
/// DECLARES a dependency and whose type binds a type from that dependency (<c>parentType</c>) makes the
/// Composer emit a signed <c>ContentReferences[]</c> edge; the install engine validates it fail-closed (a
/// missing dependency refuses fail-closed, naming it); and the graph reads the class-3 edge off the declared,
/// persisted reference. Same production handlers + <see cref="PackSeedProjector"/> the host wires — no
/// test/prod drift.
/// </summary>
public sealed class PackContentReferenceRouteTests : IAsyncLifetime
{
    private static readonly TeamId TeamA = new(Guid.Parse("aaaa0000-0000-0000-0000-0000000136c2"));
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
        var activeTeam = new MutableActiveTeamAccessor(TeamContextFor(TeamA, "Ref Co"));

        var store = new InMemoryPackInstallStore();
        var admission = new PackWorkflowAdmissionAdapter(new WorkflowAdmissionValidator());
        var installer = new PackInstaller(verifier, store, admission, new InMemoryPackInstallAudit(),
            Harborline.Api.LocalNodeHost.Tests.Authorization.TestAuthorization.AllowGate());
        var registry = _app.Services.GetRequiredService<IEntityTypeRegistry>();

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
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        _key?.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    [Fact(DisplayName = "declared reference into an installed app: composer emits it, install accepts, graph shows it")]
    public async Task Declared_reference_into_installed_app_flows_end_to_end()
    {
        // Base app FIRST (provides core-records.property), then the dependent app that DECLARES the dependency
        // and binds the type — the composer emits the signed content reference from the parsed content.
        await InstallAndActivateAsync(CoreRecordsBody());
        var install = await InstallAsync(FleetOpsBody(declareDependency: true));
        Assert.Equal(HttpStatusCode.OK, install.StatusCode); // reference resolves ⇒ install accepted
        await ActivateAsync("fleet-ops");

        using var doc = await GetJsonAsync(GraphRoute);
        var edge = Assert.Single(
            doc.RootElement.GetProperty("edges").EnumerateArray().ToList(),
            e => e.GetProperty("fromContentKey").GetString() == "fleet-ops.vehicle");
        Assert.Equal("CrossAppContentReference", edge.GetProperty("edgeClass").GetString());
        Assert.Equal("ParentOf", edge.GetProperty("relation").GetString());
        Assert.Equal("core-records", edge.GetProperty("toPackKey").GetString());
        Assert.Equal("core-records.property", edge.GetProperty("toContentKey").GetString());

        // The dependent app's "Depends on" line carries the cross-app content edge.
        using var slice = await GetJsonAsync($"{GraphRoute}?app=fleet-ops");
        Assert.Contains(slice.RootElement.GetProperty("dependsOn").EnumerateArray(),
            e => e.GetProperty("toPackKey").GetString() == "core-records"
                && e.GetProperty("edgeClass").GetString() == "CrossAppContentReference");
    }

    [Fact(DisplayName = "declared reference into a NOT-installed app: install refuses fail-closed, naming it")]
    public async Task Declared_reference_into_missing_app_refuses_fail_closed()
    {
        // Install the dependent app WITHOUT its dependency present — install must refuse, naming the missing app.
        var install = await InstallAsync(FleetOpsBody(declareDependency: true));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, install.StatusCode);

        using var doc = JsonDocument.Parse(await install.Content.ReadAsStringAsync());
        Assert.Contains(doc.RootElement.GetProperty("refusalCodes").EnumerateArray(),
            c => c.GetString() == "pack.install.refused.unmet_content_reference");

        var unmet = Assert.Single(
            doc.RootElement.GetProperty("preview").GetProperty("unmetContentReferences").EnumerateArray().ToList());
        Assert.Equal("fleet-ops.vehicle", unmet.GetProperty("fromContentKey").GetString());
        Assert.Equal("core-records", unmet.GetProperty("toPackKey").GetString()); // the missing app is named
        Assert.Equal("core-records.property", unmet.GetProperty("toContentKey").GetString());
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
        Assert.Equal(HttpStatusCode.OK, (await InstallAsync(body)).StatusCode);
        await ActivateAsync(body.GetType().GetProperty("key")!.GetValue(body)!.ToString()!);
    }

    private async Task<HttpResponseMessage> InstallAsync(object body)
    {
        var packBytes = await ExportAsync(body);
        var content = new ByteArrayContent(packBytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return await _client.PostAsync(PackInstallRoutes.InstallRoute, content);
    }

    private async Task ActivateAsync(string packKey)
    {
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

    private static object CoreRecordsBody() => new
    {
        key = "core-records",
        version = "1.0.0",
        name = "Core Records",
        description = "Base record types other apps build on.",
        scopeTier = "Horizontal",
        contents = new object[] { AssetType("core-records.property", "Property", parentType: null) },
        dependencies = Array.Empty<object>(),
        capabilityRequirements = Array.Empty<string>(),
    };

    private static object FleetOpsBody(bool declareDependency) => new
    {
        key = "fleet-ops",
        version = "1.0.0",
        name = "Fleet Ops",
        description = "Adds a Vehicle record type built on Core Records' Property.",
        scopeTier = "Horizontal",
        contents = new object[]
        {
            AssetType("fleet-ops.vehicle", "Vehicle", parentType: "core-records.property"),
        },
        dependencies = declareDependency
            ? new object[] { new { key = "core-records", version = "1.0.0" } }
            : Array.Empty<object>(),
        capabilityRequirements = Array.Empty<string>(),
    };

    private static object AssetType(string id, string displayName, string? parentType) => new
    {
        key = id,
        kind = "AssetTypeDefinition",
        version = "1.0.0",
        content = parentType is null
            ? (object)new { id, displayName }
            : new { id, displayName, parentType },
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
