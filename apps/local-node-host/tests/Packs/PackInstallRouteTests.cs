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
/// Route-level end-to-end for the Pack Composer B-1b install surface, wired to the REAL adapters: the
/// real ADR 0143 <see cref="WorkflowAdmissionValidator"/> via <see cref="PackWorkflowAdmissionAdapter"/>
/// (S-9 / A7 — no pack-scoped subset) and the install engine over an in-memory store. An own-roster form
/// pack installs (Draft) → activates (Active) → lists; an inadmissible-workflow pack is REFUSED (422) by
/// the real validator; preview never mutates. Maps <see cref="PackComposerRoutes.Map"/> +
/// <see cref="PackInstallRoutes.Map"/> exactly — no test/prod wire drift.
/// </summary>
public sealed class PackInstallRouteTests : IAsyncLifetime
{
    private static readonly TeamId TeamA = new(Guid.Parse("aaaa0000-0000-0000-0000-0000000000fd"));

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private HttpClient _unattributedClient = null!;
    private KeyPair _key = null!;
    private InMemoryPackInstallStore _store = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
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
        var activeTeam = new MutableActiveTeamAccessor(TeamContextFor(TeamA, "Team A"));

        _store = new InMemoryPackInstallStore();
        // The REAL ADR 0143 admission validator over the canonical capability→authority registry.
        var admission = new PackWorkflowAdmissionAdapter(new WorkflowAdmissionValidator());
        var installer = new PackInstaller(
            verifier,
            _store,
            admission,
            new InMemoryPackInstallAudit(),
            Harborline.Api.LocalNodeHost.Tests.Authorization.TestAuthorization.AllowGate(),
            new Harborline.Api.Foundation.Packs.Install.Compatibility.PackPlatformCompatibility(
                "2.0.0",
                PackSeedProjector.RegisteredCases));

        // The seed projector wired against a real in-memory entity-type registry (these tests do not assert on
        // projection — the AssetTypeDefinition→registry projection is covered by PackSeedProjectionRouteTests —
        // but the activate route now invokes it, so it must be present).
        var registryServices = new ServiceCollection().AddLogging().AddInMemoryAssetTypeSystem().BuildServiceProvider();
        var projector = new PackSeedProjector(
            _store, registryServices.GetRequiredService<IEntityTypeRegistry>(), NullLogger<PackSeedProjector>.Instance, time: TimeProvider.System);

        var authz = TestPackGate.AllowAll();
        _app.Use(async (http, next) =>
        {
            if (http.Request.Headers.ContainsKey("X-Test-Desktop"))
            {
                http.Features.Set(DesktopPlaneRequestFeature.Instance);
            }
            await next(http);
        });
        PackComposerRoutes.Map(_app, exporter, verifier, trustStore, signer, activeTeam, authz, TimeProvider.System, NullLogger.Instance);
        PackInstallRoutes.Map(_app, installer, _store, trustStore, PackRevocationList.Empty, activeTeam,
            authz, TimeProvider.System, NullLogger.Instance, authorizingPrincipal: _key.PrincipalId.ToBase64Url(),
            projector: projector);

        await _app.StartAsync();
        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        _client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) };
        _client.DefaultRequestHeaders.Add("X-Test-Desktop", "1");
        _unattributedClient = new HttpClient { BaseAddress = _client.BaseAddress };
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        _unattributedClient?.Dispose();
        _key?.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    [Fact(DisplayName = "install a form pack (Draft) → activate (Active) → list")]
    public async Task Install_then_activate_then_list()
    {
        var packBytes = await ExportAsync(FormPackBody());

        var install = await PostBytesAsync(PackInstallRoutes.InstallRoute, packBytes);
        Assert.Equal(HttpStatusCode.OK, install.StatusCode);
        using (var doc = JsonDocument.Parse(await install.Content.ReadAsStringAsync()))
        {
            Assert.True(doc.RootElement.GetProperty("installed").GetBoolean());
            Assert.Equal("Installed", doc.RootElement.GetProperty("action").GetString());
        }

        var activate = await _client.PostAsJsonAsync(PackInstallRoutes.ActivateRoute,
            new { packKey = "acme.pack", version = "1.0.0" });
        Assert.Equal(HttpStatusCode.OK, activate.StatusCode);

        var list = await _client.GetAsync(PackInstallRoutes.ListInstalledRoute);
        using var listDoc = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        var entry = Assert.Single(listDoc.RootElement.EnumerateArray().ToList());
        Assert.Equal("acme.pack", entry.GetProperty("packKey").GetString());
        Assert.Equal("Active", entry.GetProperty("lifecycle").GetString());
    }

    [Fact(DisplayName = "nothing installed exposes only install; first install restores the full pack surface")]
    public async Task Nothing_installed_exposes_only_install_then_restores_all_routes()
    {
        var absent = new[]
        {
            await _unattributedClient.PostAsync(PackInstallRoutes.PreviewRoute, new ByteArrayContent([1])),
            await _unattributedClient.PostAsJsonAsync(PackInstallRoutes.ActivateRoute, new { packKey = "acme.pack", version = "1.0.0" }),
            await _unattributedClient.PostAsJsonAsync(PackInstallRoutes.DeactivateRoute, new { packKey = "acme.pack", version = "1.0.0" }),
            await _unattributedClient.GetAsync(PackInstallRoutes.ListInstalledRoute),
        };

        foreach (var response in absent)
        {
            using (response)
            {
                using var unmapped = await _unattributedClient.SendAsync(new HttpRequestMessage(
                    response.RequestMessage!.Method, "/api/local-node/packs/not-a-route"));
                Assert.True(
                    response.StatusCode == unmapped.StatusCode,
                    $"Expected unavailable route '{response.RequestMessage.RequestUri}' to match an unmapped path; "
                    + $"expected {(int)unmapped.StatusCode}, actual {(int)response.StatusCode}.");
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            }
        }

        var packBytes = await ExportAsync(FormPackBody());
        Assert.Equal(HttpStatusCode.OK, (await PostBytesAsync(PackInstallRoutes.InstallRoute, packBytes)).StatusCode);

        using var preview = await PostBytesAsync(PackInstallRoutes.PreviewRoute, packBytes);
        using var activate = await _client.PostAsJsonAsync(PackInstallRoutes.ActivateRoute,
            new { packKey = "acme.pack", version = "1.0.0" });
        using var deactivate = await _client.PostAsJsonAsync(PackInstallRoutes.DeactivateRoute,
            new { packKey = "acme.pack", version = "1.0.0" });
        using var installed = await _client.GetAsync(PackInstallRoutes.ListInstalledRoute);
        Assert.NotEqual(HttpStatusCode.NotFound, preview.StatusCode);
        Assert.NotEqual(HttpStatusCode.NotFound, activate.StatusCode);
        Assert.NotEqual(HttpStatusCode.NotFound, deactivate.StatusCode);
        Assert.Equal(HttpStatusCode.OK, installed.StatusCode);
    }

    // ══ The REAL 0143 validator refuses an inadmissible workflow pack at install (S-9 / A7) ══
    [Fact(DisplayName = "install refuses an inadmissible workflow via the real 0143 validator (422)")]
    public async Task Install_refuses_an_inadmissible_workflow()
    {
        // A real workflow whose initialState references a non-existent state ⇒ InitialStateMissing.
        var body = WorkflowPackBody(initialState: "ghost");
        var packBytes = await ExportAsync(body);

        var install = await PostBytesAsync(PackInstallRoutes.InstallRoute, packBytes);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, install.StatusCode);
        var text = await install.Content.ReadAsStringAsync();
        Assert.Contains("pack.install.refused.admission", text);
        Assert.Contains("workflow.admission", text); // a real ADR 0143 violation code
        Assert.Empty(_store.ListInstalled(NodeTenantFor()));
    }

    [Fact(DisplayName = "an admissible workflow pack installs")]
    public async Task Admissible_workflow_pack_installs()
    {
        var packBytes = await ExportAsync(WorkflowPackBody(initialState: "start"));
        var install = await PostBytesAsync(PackInstallRoutes.InstallRoute, packBytes);
        Assert.Equal(HttpStatusCode.OK, install.StatusCode);
    }

    [Fact(DisplayName = "preview does not mutate + surfaces stale revocation")]
    public async Task Preview_does_not_mutate()
    {
        var installedBytes = await ExportAsync(FormPackBody());
        Assert.Equal(HttpStatusCode.OK, (await PostBytesAsync(PackInstallRoutes.InstallRoute, installedBytes)).StatusCode);
        var packBytes = await ExportAsync(FormPackBody("preview.acme.pack"));
        var preview = await PostBytesAsync(PackInstallRoutes.PreviewRoute, packBytes);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);

        using var doc = JsonDocument.Parse(await preview.Content.ReadAsStringAsync());
        Assert.Equal("WouldInstall", doc.RootElement.GetProperty("verdict").GetString());
        Assert.True(doc.RootElement.GetProperty("revocationStale").GetBoolean()); // offline: no channel list
        Assert.Single(_store.ListInstalled(NodeTenantFor()));
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

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

    private static object FormPackBody(string key = "acme.pack") => new
    {
        key,
        version = "1.0.0",
        name = "Acme Pack",
        description = "form pack",
        scopeTier = "Vertical",
        contents = new[]
        {
            new
            {
                key = "intake",
                kind = "FormDefinition",
                version = "1.0.0",
                content = new { title = "Intake", assignee = "role:approver" },
            },
        },
        dependencies = Array.Empty<object>(),
        capabilityRequirements = new[] { "forms.dynamic" },
    };

    private static object WorkflowPackBody(string initialState) => new
    {
        key = "acme.pack",
        version = "1.0.0",
        name = "Acme Pack",
        description = "workflow pack",
        scopeTier = "Vertical",
        contents = new[]
        {
            new
            {
                key = "wf",
                kind = "WorkflowDefinition",
                version = "1.0.0",
                content = new
                {
                    initialState,
                    states = new[] { new { id = "start", kind = "Terminal" } },
                    transitions = Array.Empty<object>(),
                    triggers = Array.Empty<object>(),
                    actions = Array.Empty<object>(),
                },
            },
        },
        dependencies = Array.Empty<object>(),
        capabilityRequirements = new[] { "workflow.durable" },
    };

    private static Harborline.Foundation.Assets.Common.TenantId NodeTenantFor()
        => Harborline.Api.LocalNodeHost.Data.Financial.NodeTenant.Resolve(
            new MutableActiveTeamAccessor(TeamContextFor(TeamA, "Team A")));

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
