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
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Packs.Dcp;
using Harborline.Api.Foundation.Packs.Export;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Audit;
using Harborline.Api.Foundation.Packs.Install.Trust;
using Harborline.Api.Foundation.Packs.Model;
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
/// BUILD B-1f — multi-pack same-key collisions become SURFACED, never silent (the F4 upgrade), proven
/// END-TO-END through the REAL routes (<see cref="PackComposerRoutes.Map"/> + <see cref="PackInstallRoutes.Map"/>
/// + <see cref="AssetRegistryRoutes.Map"/>) and the SAME <see cref="PackSeedProjector"/> the host wires.
/// Two packs that both ship the asset type <c>shared.equipment</c>: the collision is surfaced in the
/// install-preview; activation refuses fail-closed until an owner is chosen; a recorded choice OR a
/// declared dependency chain resolves it (the resolved owner's item projects, the loser defers); and a
/// second category-provider is refused activation into an occupied slot.
/// </summary>
public sealed class PackMultiPackCollisionRouteTests : IAsyncLifetime
{
    private static readonly TeamId TeamA = new(Guid.Parse("aaaa0000-0000-0000-0000-0000000001f0"));

    private const string AssetTypesRoute = "/api/local-node/asset-registry/types";
    private const string SharedKey = "shared.equipment";

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private KeyPair _key = null!;
    private InMemoryPackInstallStore _store = null!;
    private InMemoryPackInstallAudit _audit = null!;
    private PackInstaller _installer = null!;
    private PackSeedProjector _projector = null!;
    private Harborline.Api.Foundation.Packs.Install.Compatibility.PackPlatformCompatibility _platform = null!;

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
        var activeTeam = new MutableActiveTeamAccessor(TeamContextFor(TeamA, "General Co"));

        _store = new InMemoryPackInstallStore();
        _audit = new InMemoryPackInstallAudit();
        var admission = new PackWorkflowAdmissionAdapter(new WorkflowAdmissionValidator());
        _installer = new PackInstaller(verifier, _store, admission, _audit,
            Harborline.Api.LocalNodeHost.Tests.Authorization.TestAuthorization.AllowGate());

        var registry = _app.Services.GetRequiredService<IEntityTypeRegistry>();
        // Platform facts on BOTH the projector and the routes (ticket 160): the projector re-checks
        // per pass; the routes mark refused packs on GET /packs/installed. The fixture's packs declare
        // no requirements, so every pre-existing test is unaffected.
        _platform = new Harborline.Api.Foundation.Packs.Install.Compatibility.PackPlatformCompatibility(
            "1.0.0",
            [new Harborline.Api.Foundation.Packs.Install.Compatibility.PackProjectorCase(
                PackContentKind.AssetTypeDefinition, ["assets.registry"])]);
        _projector = new PackSeedProjector(
            _store, registry, NullLogger<PackSeedProjector>.Instance, platform: _platform, time: TimeProvider.System);

        _app.Use(async (http, next) =>
        {
            http.Features.Set(DesktopPlaneRequestFeature.Instance);
            await next(http);
        });
        PackComposerRoutes.Map(_app, exporter, verifier, trustStore, signer, activeTeam,
            TestPackGate.AllowAll(), TimeProvider.System, NullLogger.Instance);
        PackInstallRoutes.Map(_app, _installer, _store, trustStore, PackRevocationList.Empty, activeTeam,
            TestPackGate.AllowAll(), TimeProvider.System, NullLogger.Instance,
            authorizingPrincipal: _key.PrincipalId.ToBase64Url(), projector: _projector,
            platform: _platform);
        AssetRegistryRoutes.Map(
            _app.MapDeviceReachableProductDataGroup(),
            registry,
            _app.Services.GetRequiredService<IRegistryEntityRepository>(),
            _app.Services.GetRequiredService<ITypedRelationshipStore>(),
            _app.Services.GetRequiredService<IConditionAssessmentStore>(),
            _app.Services.GetRequiredService<IFormSubmissionRecordStore>(),
            activeTeam,
            TimeProvider.System);

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

    [Fact(DisplayName = "install-preview of a second pack sharing an asset-type key SURFACES the collision (both pack ids + key)")]
    public async Task Preview_surfaces_the_collision()
    {
        await InstallAsync(PackBody("pack.a", (SharedKey, "Equipment A"), ("a.only", "A Only")));

        var previewBytes = await ExportAsync(PackBody("pack.b", (SharedKey, "Equipment B"), ("b.only", "B Only")));
        var preview = await PreviewAsync(previewBytes);

        var collisions = preview.GetProperty("crossPackCollisions").EnumerateArray().ToList();
        var collision = Assert.Single(collisions);
        Assert.Equal(SharedKey, collision.GetProperty("contentKey").GetString());
        var claimants = collision.GetProperty("claimingPackKeys").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("pack.a", claimants);
        Assert.Contains("pack.b", claimants);
        Assert.Equal("RequiresChoice", collision.GetProperty("resolution").GetString());
        // Install stays additive (S-2) — the collision does not refuse the install plan itself.
        Assert.Equal("WouldInstall", preview.GetProperty("verdict").GetString());
    }

    [Fact(DisplayName = "activate REFUSES while a cross-pack collision is unresolved (fail-closed, honest detail)")]
    public async Task Activate_refuses_unresolved_collision()
    {
        await InstallAsync(PackBody("pack.a", (SharedKey, "Equipment A")));
        await InstallAsync(PackBody("pack.b", (SharedKey, "Equipment B")));

        var refused = await ActivateAsync("pack.a", "1.0.0");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        var body = await ReadJsonAsync(refused);
        Assert.Equal(PackInstallCodes.ActivateUnresolvedCollision, body.GetProperty("error").GetString());
        Assert.Contains(SharedKey, body.GetProperty("detail").GetString());
        Assert.Contains("pack.b", body.GetProperty("detail").GetString());

        // The contested type never went live (fail-closed).
        Assert.DoesNotContain(SharedKey, await GetTypeIdsAsync());
    }

    [Fact(DisplayName = "a recorded owning-pack choice → the OWNER's item projects even if the loser activated first")]
    public async Task Recorded_choice_projects_the_owner()
    {
        await InstallAsync(PackBody("pack.a", (SharedKey, "Equipment A"), ("a.only", "A Only")));
        await InstallAsync(PackBody("pack.b", (SharedKey, "Equipment B"), ("b.only", "B Only")));

        // Choose pack.b as the owner, then activate the NON-owner (pack.a) FIRST — ownership must still win.
        Assert.Equal(HttpStatusCode.OK,
            (await ActivateAsync("pack.a", "1.0.0", (SharedKey, "pack.b"))).StatusCode);
        // pack.a is the non-owner: shared.equipment defers, but its own key projects.
        var afterA = await GetTypeIdsAsync();
        Assert.Contains("a.only", afterA);
        Assert.DoesNotContain(SharedKey, afterA);

        Assert.Equal(HttpStatusCode.OK, (await ActivateAsync("pack.b", "1.0.0")).StatusCode);

        // The owner (pack.b) projected shared.equipment with ITS displayName; both packs' unique types live.
        var types = await GetTypesAsync();
        var shared = types.Single(t => t.GetProperty("id").GetString() == SharedKey);
        Assert.Equal("Equipment B", shared.GetProperty("displayName").GetString());
        Assert.Equal("Pack", shared.GetProperty("provenance").GetString());
        var ids = types.Select(t => t.GetProperty("id").GetString()).ToHashSet();
        Assert.Contains("a.only", ids);
        Assert.Contains("b.only", ids);
    }

    [Fact(DisplayName = "retracting inactive pack 2 never removes pack 1's surviving shared-key ownership")]
    public async Task Inactive_non_owner_retraction_preserves_active_owner()
    {
        await InstallAsync(PackBody("pack.a", (SharedKey, "Equipment A")));
        await InstallAsync(PackBody("pack.b", (SharedKey, "Equipment B")));

        Assert.Equal(HttpStatusCode.OK,
            (await ActivateAsync("pack.a", "1.0.0", (SharedKey, "pack.a"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ActivateAsync("pack.b", "1.0.0")).StatusCode);

        var before = (await GetTypesAsync()).Single(t => t.GetProperty("id").GetString() == SharedKey);
        Assert.Equal("Equipment A", before.GetProperty("displayName").GetString());

        var tenant = NodeTenantFor();
        var deactivated = _installer.Deactivate(tenant, "pack.b", "1.0.0", DateTimeOffset.UnixEpoch, "test-operator");
        Assert.True(deactivated.Deactivated);
        Assert.Equal(
            PackLifecycleState.Inactive,
            _store.GetVersion(tenant, "pack.b", "1.0.0")!.Lifecycle);

        var retraction = await _projector.ProjectActivePacksAsync(tenant);

        // A final-state-only assertion is insufficient: a broken pass could retract pack.a's seed and then
        // restore it while re-projecting active packs. This count proves pack.b never removed it at all.
        Assert.Equal(0, retraction.AssetTypesRetracted);
        Assert.Equal(0, retraction.AssetTypesSeeded);

        var after = (await GetTypesAsync()).Single(t => t.GetProperty("id").GetString() == SharedKey);
        Assert.Equal("Equipment A", after.GetProperty("displayName").GetString());
    }

    [Fact(DisplayName = "a declared dependency chain resolves the collision SILENTLY (owner projects, no choice recorded)")]
    public async Task Dependency_chain_resolves_silently()
    {
        // pack.a depends on pack.b ⇒ pack.a composes over ⇒ pack.a owns shared.equipment (D5), no ceremony.
        // Ticket 152: install presence-checks declared dependencies fail-closed, so the DEPENDENCY
        // installs first; the pin here is the ACTIVATION-order freedom below, which is unchanged.
        await InstallAsync(PackBody("pack.b", (SharedKey, "Equipment B")));
        await InstallAsync(PackBody("pack.a", new[] { (SharedKey, "Equipment A") }, dependsOn: "pack.b"));

        // Activate in EITHER order, no resolutions recorded — the chain alone decides.
        Assert.Equal(HttpStatusCode.OK, (await ActivateAsync("pack.b", "1.0.0")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ActivateAsync("pack.a", "1.0.0")).StatusCode);

        var shared = (await GetTypesAsync()).Single(t => t.GetProperty("id").GetString() == SharedKey);
        Assert.Equal("Equipment A", shared.GetProperty("displayName").GetString()); // the dependency-chain owner.
    }

    [Fact(DisplayName = "provider-slot exclusivity: activating a 2nd provider in an occupied category is REFUSED at activate")]
    public async Task Provider_slot_activation_is_exclusive()
    {
        // Two DIFFERENT providers of one category (distinct content keys, same slot) — both install (additive).
        await InstallAsync(PackBody("payments.stripe", new[] { ("stripe.gateway", "Stripe Gateway") }, providerSlot: "payments"));
        await InstallAsync(PackBody("payments.adyen", new[] { ("adyen.gateway", "Adyen Gateway") }, providerSlot: "payments"));

        Assert.Equal(HttpStatusCode.OK, (await ActivateAsync("payments.stripe", "1.0.0")).StatusCode);

        var refused = await ActivateAsync("payments.adyen", "1.0.0");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        var body = await ReadJsonAsync(refused);
        Assert.Equal(PackInstallCodes.ActivateProviderSlotOccupied, body.GetProperty("error").GetString());
        Assert.Contains("payments.stripe", body.GetProperty("detail").GetString());
    }

    [Theory(DisplayName = "two committed first-party packs coexist deterministically; deactivating pack 2 "
        + "retracts only its Active contribution set")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_second_pack_deactivation_is_order_independent(bool reverseInstallOrder)
    {
        var generalBytes = await ExportFixtureAsync("general-pack.export.json");
        var notesBytes = await ExportFixtureAsync("notes-lite-pack.export.json");
        var ordered = reverseInstallOrder
            ? new[] { notesBytes, generalBytes }
            : new[] { generalBytes, notesBytes };

        foreach (var bytes in ordered)
        {
            Assert.Equal(HttpStatusCode.OK, (await PostBytesAsync(PackInstallRoutes.InstallRoute, bytes)).StatusCode);
        }

        var activationOrder = reverseInstallOrder
            ? new[] { "harborline.notes-lite", "harborline.general" }
            : new[] { "harborline.general", "harborline.notes-lite" };
        foreach (var packKey in activationOrder)
        {
            Assert.Equal(HttpStatusCode.OK, (await ActivateAsync(packKey, "1.0.0")).StatusCode);
        }

        // The composed Active contribution set is deterministic regardless of install/activation order.
        var activeBefore = ActivePacks();
        Assert.Equal(new[] { "harborline.general", "harborline.notes-lite" },
            activeBefore.Select(p => p.PackKey).Order(StringComparer.Ordinal));
        Assert.Equal(
            new[]
            {
                "general.equipment", "general.facility", "general.furniture", "general.invoice",
                "general.it-computer", "general.tool", "general.vehicle", "notes.capture", "notes.entry",
            },
            activeBefore.SelectMany(p => p.SeedItems).Select(i => i.Key).Order(StringComparer.Ordinal));

        var deactivate = await _client.PostAsJsonAsync(PackInstallRoutes.DeactivateRoute,
            new { packKey = "harborline.notes-lite", version = "1.0.0" });
        Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);

        var activeAfter = ActivePacks();
        var general = Assert.Single(activeAfter);
        Assert.Equal("harborline.general", general.PackKey);
        Assert.DoesNotContain(general.SeedItems, i => i.Key is "notes.entry" or "notes.capture");

        // Deactivation is a pointer flip, not uninstall/purge: pack 2's immutable seeds + watermark remain.
        var notes = _store.GetVersion(NodeTenantFor(), "harborline.notes-lite", "1.0.0");
        Assert.NotNull(notes);
        Assert.Equal(PackLifecycleState.Inactive, notes!.Lifecycle);
        Assert.Equal(new[] { "notes.capture", "notes.entry" },
            notes.SeedItems.Select(i => i.Key).Order(StringComparer.Ordinal));
        Assert.NotNull(_store.GetWatermark(NodeTenantFor(), "harborline.notes-lite"));
        Assert.Contains(_audit.Query(NodeTenantFor()), e =>
            e.Action == PackInstallAuditAction.Deactivated && e.PackKey == "harborline.notes-lite");

        // The pointer flip is reversible and restores the same immutable contribution set.
        Assert.Equal(HttpStatusCode.OK,
            (await ActivateAsync("harborline.notes-lite", "1.0.0")).StatusCode);
        Assert.Equal(new[] { "harborline.general", "harborline.notes-lite" },
            ActivePacks().Select(p => p.PackKey).Order(StringComparer.Ordinal));
    }

    [Fact(DisplayName = "activate response SURFACES a sibling active pack's platform refusal; GET installed marks it")]
    public async Task Activate_response_carries_sibling_platform_refusal()
    {
        // A stranded sibling: legitimately activated when the platform was in-window, now requiring a
        // capability this build does not provide. Committed via the STORE (the installer would refuse
        // the requirement today — that admission is not the seam under test).
        const string strandedJson = /*lang=json,strict*/
            "{\"id\":\"stranded.type\",\"displayName\":\"Stranded\",\"traits\":[\"Maintainable\"]}";
        var stranded = new InstalledPack(
            "compat.stranded",
            "1.0.0",
            PackScopeTier.Horizontal,
            PackLifecycleState.Draft,
            [new PackSeedItem(
                "stranded.type", PackContentKind.AssetTypeDefinition, "1.0.0", strandedJson,
                Harborline.Api.Foundation.Blobs.Cid.FromBytes(System.Text.Encoding.UTF8.GetBytes(strandedJson)))],
            new Dictionary<string, int>(),
            DateTimeOffset.UtcNow,
            _key.PrincipalId,
            1,
            TrustScope.OwnRoster,
            Array.Empty<PackDependencyRef>(),
            CapabilityRequirements: ["packs.pillar.future"]);
        var tenant = NodeTenantFor();
        _store.Commit(new PackInstallTransaction(
            tenant, stranded,
            new PackInstallWatermark(stranded.PackKey, stranded.Version, new Dictionary<string, int>()),
            Array.Empty<PackTenantOverride>()));
        _store.Activate(tenant, stranded.PackKey, stranded.Version);

        // Activating an unrelated in-window pack runs the shared projection pass — the response must
        // CARRY the sibling's pack-grain refusal, not silently drop it (review item 5b).
        await InstallAsync(PackBody("pack.fresh", ("fresh.type", "Fresh Type")));
        var activate = await ActivateAsync("pack.fresh", "1.0.0");
        Assert.Equal(HttpStatusCode.OK, activate.StatusCode);

        var body = await ReadJsonAsync(activate);
        var refusal = Assert.Single(body.GetProperty("platformRefusals").EnumerateArray().ToList());
        Assert.Equal("compat.stranded", refusal.GetProperty("packKey").GetString());
        Assert.Equal("pack.projection.platform_incompatible", refusal.GetProperty("code").GetString());
        var unmet = Assert.Single(refusal.GetProperty("unmet").EnumerateArray().ToList());
        Assert.Equal("packs.pillar.future", unmet.GetProperty("capability").GetString());

        // The fresh pack projected; the stranded one did not.
        var ids = await GetTypeIdsAsync();
        Assert.Contains("fresh.type", ids);
        Assert.DoesNotContain("stranded.type", ids);

        // And the list surface marks the Active-but-refused pack (an operator can SEE it).
        var installed = await ReadJsonAsync(await _client.GetAsync(PackInstallRoutes.ListInstalledRoute));
        var rows = installed.EnumerateArray().ToList();
        Assert.True(rows.Single(r => r.GetProperty("packKey").GetString() == "compat.stranded")
            .GetProperty("platformRefused").GetBoolean());
        Assert.False(rows.Single(r => r.GetProperty("packKey").GetString() == "pack.fresh")
            .GetProperty("platformRefused").GetBoolean());
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

    private async Task InstallAsync(object body)
    {
        var bytes = await ExportAsync(body);
        var install = await PostBytesAsync(PackInstallRoutes.InstallRoute, bytes);
        Assert.Equal(HttpStatusCode.OK, install.StatusCode);
    }

    private async Task<byte[]> ExportAsync(object body)
    {
        var resp = await _client.PostAsJsonAsync(PackComposerRoutes.ExportRoute, body);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return await resp.Content.ReadAsByteArrayAsync();
    }

    private async Task<byte[]> ExportFixtureAsync(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Packs", "Fixtures", fileName);
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        return await ExportAsync(doc.RootElement.Clone());
    }

    private List<InstalledPack> ActivePacks()
        => _store.ListInstalled(NodeTenantFor())
            .Where(p => p.Lifecycle == PackLifecycleState.Active)
            .OrderBy(p => p.PackKey, StringComparer.Ordinal)
            .ToList();

    private static TenantId NodeTenantFor() => TenantId.FromString(TeamA.Value.ToString("D"));

    private async Task<JsonElement> PreviewAsync(byte[] bytes)
    {
        var resp = await PostBytesAsync(PackInstallRoutes.PreviewRoute, bytes);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return await ReadJsonAsync(resp);
    }

    private Task<HttpResponseMessage> ActivateAsync(string packKey, string version, params (string ContentKey, string OwningPackKey)[] resolutions)
        => _client.PostAsJsonAsync(PackInstallRoutes.ActivateRoute, new
        {
            packKey,
            version,
            resolutions = resolutions.Select(r => new { contentKey = r.ContentKey, owningPackKey = r.OwningPackKey }).ToArray(),
        });

    private async Task<List<JsonElement>> GetTypesAsync()
    {
        var resp = await _client.GetAsync(AssetTypesRoute);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("types").EnumerateArray().Select(e => e.Clone()).ToList();
    }

    private async Task<List<string?>> GetTypeIdsAsync()
        => (await GetTypesAsync()).Select(t => t.GetProperty("id").GetString()).ToList();

    private Task<HttpResponseMessage> PostBytesAsync(string route, byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return _client.PostAsync(route, content);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage resp)
    {
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    /// <summary>An export body for a pack shipping one or more asset types, with optional dependency + slot.</summary>
    private static object PackBody(string key, params (string Id, string Display)[] types)
        => PackBody(key, types, dependsOn: null, providerSlot: null);

    private static object PackBody(string key, (string Id, string Display)[] types, string? dependsOn = null, string? providerSlot = null)
        => new
        {
            key,
            version = "1.0.0",
            name = key,
            description = "B-1f collision test pack",
            scopeTier = "Horizontal",
            contents = types.Select(t => AssetType(t.Id, t.Display)).ToArray(),
            dependencies = dependsOn is null
                ? Array.Empty<object>()
                : new object[] { new { key = dependsOn, version = "1.0.0" } },
            capabilityRequirements = Array.Empty<string>(),
            providerSlot,
        };

    private static object AssetType(string id, string displayName) => new
    {
        key = id,
        kind = "AssetTypeDefinition",
        version = "1.0.0",
        content = new
        {
            id,
            displayName,
            traits = new[] { "Maintainable" },
            expectedUsefulLifeYears = 10,
            conditionScaleMax = 5,
        },
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
