using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Harborline.Api.Foundation.Packs.Dcp;
using Harborline.Api.Foundation.Packs.DependencyInjection;
using Harborline.Api.Foundation.Packs.Export;
using Harborline.Api.Foundation.Packs.Model;

using Harborline.Api.Kernel.Security.Keys;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Packs;
using Harborline.Api.LocalNodeHost.Health;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Harborline.Api.LocalNodeHost.Tests.Health;

/// <summary>Default shipping-host Layout capability admission and historical Layout state preservation.</summary>
public sealed partial class ComposedHostBootSmokeTests
{
    [Fact]
    public async Task Program_Composition_DefaultLayoutAdmissionRefusesAcrossPackSurfaces()
        => await AssertDefaultLayoutPackRefusalAsync(legacyScenario: null);

    [Fact]
    public async Task Program_Composition_DefaultLayoutWithoutRequirementsRefusesWholePackAsCapabilityUndeclared()
        => await AssertDefaultLayoutPackRefusalAsync(legacyScenario: null, emptyRequirements: true);

    [Fact]
    public async Task Program_Composition_MissingLayoutBindingKindRefusesAndPreservesHistoricalDraftState()
        => await AssertDefaultLayoutPackRefusalAsync(legacyScenario: "upgrade", missingBindingKind: true);

    [Fact]
    public async Task Program_Composition_LayoutRefusalPreservesHistoricalActiveState()
        => await AssertDefaultLayoutPackRefusalAsync(legacyScenario: "upgrade", historicalActive: true);

    [Theory]
    [InlineData("upgrade")]
    [InlineData("reimport")]
    [InlineData("mixed")]
    public async Task Program_Composition_LayoutRefusalPreservesHistoricalDraftState(string legacyScenario)
        => await AssertDefaultLayoutPackRefusalAsync(legacyScenario);

    private static async Task AssertDefaultLayoutPackRefusalAsync(string? legacyScenario,
        bool emptyRequirements = false, bool missingBindingKind = false, bool historicalActive = false)
    {
        const string rootSeedHex =
            "4646464646464646464646464646464646464646464646464646464646464646";
        await using var first = ComposedHost.Start(
            "default Layout pack admission", webClient: false, llmProxy: false,
            schedulingDogfood: false, multiTeam: false, environment: "Production", rootSeedHex: rootSeedHex);
        using var firstClient = LayoutAdmissionFounderClient(await first.AwaitReadinessAsync());
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddPackComposerExportVerify();
        services.AddSqlCipherLocalNodeDbContext(Convert.FromHexString(rootSeedHex),
            Path.Combine(first.DataDirectory, "local-node.db"), new SqlCipherKeyDerivation(), pooling: false);
        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<NodeLocalPacksDbContext>>();
        await InstallLayoutAdmissionNavigationAsync(firstClient, first.Deadline);
        var tenant = await PrepareLayoutAdmissionBaselineAsync(firstClient, first.Deadline);
        ComposedHost? restarted = null;
        HttpClient? restartedClient = null;
        try
        {
            var client = firstClient;
            var deadline = first.Deadline;
            if (legacyScenario is not null)
            {
                await first.StopAsync();
                SeedHistoricalLayout(factory, tenant, rootSeedHex, historicalActive);
                restarted = ComposedHost.Start(
                    historicalActive ? "historical Active Layout preservation" : "historical Draft Layout preservation",
                    webClient: false, llmProxy: false,
                    schedulingDogfood: false, multiTeam: false, environment: "Production",
                    rootSeedHex: rootSeedHex, dataDirectoryOverride: first.DataDirectory);
                restartedClient = LayoutAdmissionFounderClient(await restarted.AwaitReadinessAsync());
                client = restartedClient;
                deadline = restarted.Deadline;
            }

            var version = legacyScenario == "reimport" ? "1.0.0" : "2.0.0";
            var packKey = legacyScenario is null ? "layout-admission.new-layout" : "layout-admission.historical-layout";
            var definition = JsonSerializer.Deserialize<JsonElement>(
                LayoutAdmissionDefinitionJson(version, tenant, emptyRequirements));
            if (emptyRequirements)
                Assert.Empty(definition.GetProperty("envelope").GetProperty("requires").EnumerateArray());
            if (missingBindingKind)
            {
                var malformed = System.Text.Json.Nodes.JsonNode.Parse(definition.GetRawText())!;
                var binding = malformed["blocks"]![0]!["binding"]!.AsObject();
                Assert.True(binding.Remove("binding_kind"));
                Assert.False(binding.ContainsKey("binding_kind"));
                definition = JsonSerializer.SerializeToElement(malformed);
                Assert.False(definition.GetProperty("blocks")[0].GetProperty("binding")
                    .TryGetProperty("binding_kind", out _));
            }
            var contents = new List<PackContentSource>
            {
                new("layout-admission.layout", PackContentKind.Layout, version,
                    JsonNode.Parse(definition.GetRawText())!),
            };
            if (legacyScenario == "mixed")
                contents.Add(new("layout-admission.mixed-navigation", PackContentKind.NavWorkspaceConfig, version,
                    JsonSerializer.SerializeToNode(new { seedWorkspaces = new[]
                    {
                        new { id = "layout-admission-refused-workspace", labelKey = "workspaces.layout-admission-refused" },
                    } })!));
            var bytes = await BuildLayoutAdmissionPackAsync(provider.GetRequiredService<IPackExporter>(),
                rootSeedHex, packKey, version, contents, deadline);
            var baseline = await ReadLayoutAdmissionStateAsync(factory, tenant, client,
                legacyScenario is not null, deadline, historicalActive);

            foreach (var route in new[]
                { PackInstallRoutes.PreviewRoute, PackInstallRoutes.CheckRoute, PackInstallRoutes.InstallRoute })
            {
                using var content = new ByteArrayContent(bytes);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                using var response = await client.PostAsync(route, content, deadline);
                var body = await response.Content.ReadAsStringAsync(deadline);
                Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
                Assert.Equal(route == PackInstallRoutes.InstallRoute
                    ? HttpStatusCode.UnprocessableEntity : HttpStatusCode.OK, response.StatusCode);
                using var document = JsonDocument.Parse(body);
                var result = document.RootElement;
                if (route == PackInstallRoutes.InstallRoute)
                {
                    Assert.False(result.GetProperty("installed").GetBoolean(), body);
                    Assert.Equal("install", result.GetProperty("stage").GetString());
                    Assert.Equal("Refused", result.GetProperty("preview").GetProperty("verdict").GetString());
                    AssertLayoutAdmissionRefusal(result.GetProperty("preview"), version, emptyRequirements, missingBindingKind);
                }
                else
                    Assert.Equal("Refused", result.GetProperty("verdict").GetString());
                AssertLayoutAdmissionRefusal(result, version, emptyRequirements, missingBindingKind,
                    expectIdentityUnknown: legacyScenario == "reimport" && route == PackInstallRoutes.CheckRoute);
                Assert.Equal(baseline,
                    await ReadLayoutAdmissionStateAsync(factory, tenant, client,
                        legacyScenario is not null, deadline, historicalActive));
            }
        }
        finally
        {
            restartedClient?.Dispose();
            // Stop both processes before either ComposedHost can delete their shared directory.
            await first.StopAsync();
            if (restarted is not null)
            {
                await restarted.StopAsync();
                await restarted.DisposeAsync();
            }
        }
    }

    private static HttpClient LayoutAdmissionFounderClient(Uri baseUri)
    {
        var client = new HttpClient { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "composed-host-smoke-token");
        return client;
    }

    private static async Task<byte[]> BuildLayoutAdmissionPackAsync(IPackExporter exporter, string rootSeedHex,
        string packKey, string version, IReadOnlyList<PackContentSource> contents, CancellationToken deadline)
    {
        // Layout's signed envelope requires tenant metadata. The HTTP composer correctly refuses
        // tenant-named JSON fields, so prepare the artifact with the production exporter instead.
        // Preview/check/install below still use the real shipping host and its own-roster verifier.
        using var signer = new NodePrincipalSigner(Convert.FromHexString(rootSeedHex));
        var request = new PackExportRequest(packKey, version, "Layout capability refusal", "Default host admission",
            PackScopeTier.Vertical, contents, Array.Empty<PackDependencyRef>(), Array.Empty<string>(),
            PackComposerRoutes.OwnRosterEpoch,
            Dcp: DomainComplianceProfile.General(signer.Signer.IssuerId.ToBase64Url()));
        var outcome = await exporter.ExportAsync(request, signer.Signer, deadline);
        Assert.True(outcome.Validation.IsValid,
            string.Join(", ", outcome.Validation.Errors.Select(error => $"{error.Code}:{error.Target}")));
        Assert.True(outcome.Succeeded);
        Assert.NotNull(outcome.FileBytes);
        return outcome.FileBytes;
    }

    private static async Task<byte[]> ExportLayoutAdmissionPackAsync(HttpClient client, object request, CancellationToken deadline)
    {
        using var response = await client.PostAsJsonAsync(PackComposerRoutes.ExportRoute, request, deadline);
        var bytes = await response.Content.ReadAsByteArrayAsync(deadline);
        Assert.True(response.StatusCode == HttpStatusCode.OK, Encoding.UTF8.GetString(bytes));
        return bytes;
    }

    private static async Task InstallLayoutAdmissionNavigationAsync(HttpClient client, CancellationToken deadline)
    {
        var bytes = await ExportLayoutAdmissionPackAsync(client, new
        {
            key = "layout-admission.navigation", version = "1.0.0", name = "Layout admission navigation control",
            description = "Layout-free install control", scopeTier = "Horizontal",
            contents = new[]
            {
                new { key = "layout-admission.navigation.chrome", kind = "NavWorkspaceConfig", version = "1.0.0",
                    content = new { seedWorkspaces = new[]
                    {
                        new { id = "layout-admission-preserved-workspace", labelKey = "workspaces.layout-admission-preserved" },
                    } } },
            },
            dependencies = Array.Empty<object>(),
        }, deadline);
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var install = await client.PostAsync(PackInstallRoutes.InstallRoute, content, deadline);
        var installBody = await install.Content.ReadAsStringAsync(deadline);
        Assert.Equal(HttpStatusCode.OK, install.StatusCode);
        using var installed = JsonDocument.Parse(installBody);
        Assert.True(installed.RootElement.GetProperty("installed").GetBoolean(), installBody);
        using var activation = await client.PostAsJsonAsync(PackInstallRoutes.ActivateRoute,
            new { packKey = "layout-admission.navigation", version = "1.0.0" }, deadline);
        Assert.Equal(HttpStatusCode.OK, activation.StatusCode);
        var activated = await activation.Content.ReadFromJsonAsync<ActivatePackResponseDto>(deadline);
        Assert.NotNull(activated);
        Assert.True(activated.Activated);
        Assert.Empty(activated.ProjectionRefusals);
        Assert.Empty(activated.PlatformRefusals ?? []);
    }

    private static async Task<string> PrepareLayoutAdmissionBaselineAsync(HttpClient client, CancellationToken deadline)
    {
        using var response = await client.GetAsync(ConfigurationActivationRoutes.EffectiveRoute, deadline);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var effective = await response.Content.ReadFromJsonAsync<EffectiveGenerationDto>(deadline);
        Assert.NotNull(effective);
        var references = effective.References;
        var activeKeys = references.GetProperty("activePackageKeys").EnumerateArray()
            .Select(key => key.GetString()!).ToArray();
        Assert.Contains("layout-admission.navigation", activeKeys);
        Assert.Contains("harborline.platform", activeKeys);
        var ownership = references.GetProperty("ownership").EnumerateArray()
            .Select(owner => new ConfigurationOwnershipDto(owner.GetProperty("definitionKey").GetString()!,
                owner.GetProperty("packageKey").GetString()!)).ToArray();
        Assert.NotEmpty(ownership);
        using var prepare = await client.PostAsJsonAsync(ConfigurationActivationRoutes.PrepareRoute,
            new PrepareConfigurationRequestDto(effective.Digest, activeKeys, ownership), deadline);
        var prepareBody = await prepare.Content.ReadAsStringAsync(deadline);
        Assert.True(prepare.StatusCode == HttpStatusCode.OK, prepareBody);
        using var prepared = JsonDocument.Parse(prepareBody);
        Assert.Equal("preparing", prepared.RootElement.GetProperty("status").GetString());
        Assert.False(string.IsNullOrWhiteSpace(prepared.RootElement.GetProperty("projection")
            .GetProperty("digest").GetString()));
        var candidate = prepared.RootElement.GetProperty("candidateDigest").GetString()!;
        using var activate = await client.PostAsJsonAsync(ConfigurationActivationRoutes.ActivateRoute,
            new ActivateConfigurationRequestDto(effective.Digest, candidate,
                new EvidenceIntentDto(Guid.NewGuid().ToString("N"), "Layout admission governed baseline")), deadline);
        var activateBody = await activate.Content.ReadAsStringAsync(deadline);
        Assert.True(activate.StatusCode == HttpStatusCode.OK, activateBody);
        using var activated = JsonDocument.Parse(activateBody);
        Assert.Equal("effective", activated.RootElement.GetProperty("status").GetString());
        Assert.Equal(candidate, activated.RootElement.GetProperty("effectiveDigest").GetString());
        return references.GetProperty("tenantKey").GetString()!;
    }

    private static string LayoutAdmissionDefinitionJson(string version, string tenant, bool emptyRequirements = false)
        => Encoding.UTF8.GetString(Harborline.Blocks.BuilderDefinitions.LayoutDefinitionJson.SerializeCanonical(
            new Harborline.Blocks.BuilderDefinitions.LayoutDefinition(
                new Harborline.Blocks.BuilderDefinitions.LayoutDefinitionEnvelope(
                    "layout-admission.layout", version, tenant,
                    Harborline.Blocks.BuilderDefinitions.LayoutCascadeLayer.TenantConfiguration,
                    JsonSerializer.SerializeToElement(new { source = "layout-admission-historical-layout" }), "standard", false,
                    emptyRequirements
                        ? []
                        : [new Harborline.Blocks.BuilderDefinitions.LayoutDefinitionRequirement("platform.layout", "1.0.0")],
                    new Harborline.Foundation.Definitions.DefinitionContractVersion(1, 0)),
                1, Harborline.Blocks.BuilderDefinitions.LayoutMedium.Screen,
                Harborline.Blocks.BuilderDefinitions.LayoutIntent.Observe,
                [new Harborline.Blocks.BuilderDefinitions.LayoutBlock("title", "layout.text",
                    new Harborline.Blocks.BuilderDefinitions.LayoutStaticBinding(
                        JsonSerializer.SerializeToElement("Historical Layout declaration")), [])],
                [], [], [], null, [])));

    private static void SeedHistoricalLayout(IDbContextFactory<NodeLocalPacksDbContext> factory,
        string tenant, string rootSeedHex, bool historicalActive)
    {
        using var signer = new NodePrincipalSigner(Convert.FromHexString(rootSeedHex));
        var floors = new Dictionary<string, int>(StringComparer.Ordinal) { ["scoring"] = 3 };
        Harborline.Api.Foundation.Packs.Install.PackSeedItem Seed(string key,
            Harborline.Api.Foundation.Packs.Model.PackContentKind kind, string json)
            => new(key, kind, "1.0.0", json,
                Harborline.Api.Foundation.Blobs.Cid.FromBytes(Encoding.UTF8.GetBytes(json)));
        var pack = new Harborline.Api.Foundation.Packs.Install.InstalledPack(
            "layout-admission.historical-layout", "1.0.0", Harborline.Api.Foundation.Packs.Model.PackScopeTier.Vertical,
            Harborline.Api.Foundation.Packs.Install.PackLifecycleState.Draft,
            [Seed("layout-admission.layout", Harborline.Api.Foundation.Packs.Model.PackContentKind.Layout,
                LayoutAdmissionDefinitionJson("1.0.0", tenant))],
            floors, new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), signer.Signer.IssuerId,
            1, Harborline.Api.Foundation.Packs.Trust.TrustScope.OwnRoster, []);
        var tenantId = new Harborline.Foundation.Assets.Common.TenantId(tenant);
        var store = new DurablePackInstallStore(factory);
        store.Commit(new(
            tenantId, pack,
            new Harborline.Api.Foundation.Packs.Install.PackInstallWatermark(pack.PackKey, "1.0.0", floors),
            [new Harborline.Api.Foundation.Packs.Install.PackTenantOverride("layout-admission.layout",
                System.Text.Json.Nodes.JsonNode.Parse("""{"intent":"Observe"}""")!)]));
        if (historicalActive)
        {
            store.Activate(tenantId, pack.PackKey, "1.0.0");
            Assert.Equal("1.0.0", store.GetActive(tenantId, "layout-admission.historical-layout")?.Version);
        }
    }

    private static void AssertLayoutAdmissionRefusal(JsonElement result, string version,
        bool emptyRequirements = false, bool missingBindingKind = false, bool expectIdentityUnknown = false)
    {
        var expectedCode = missingBindingKind ? "layout.definition.body_invalid"
            : emptyRequirements ? "layout.pack.capability_undeclared" : "layout.pack.capability_unsupported";
        var expectedPointer = missingBindingKind ? ""
            : emptyRequirements ? "/envelope/requires" : "/envelope/requires/0/minimum_platform_version";
        var expectedCodes = expectIdentityUnknown
            ? new[] { expectedCode, "pack.install.refused.same_version_identity_unknown" }
            : new[] { expectedCode };
        Assert.Equal(expectedCodes, result.GetProperty("refusalCodes").EnumerateArray()
            .Select(code => code.GetString()));
        var refusals = result.GetProperty("refusals").EnumerateArray().ToArray();
        Assert.Equal(expectedCodes.Length, refusals.Length);
        var refusal = refusals[0];
        Assert.Equal(expectedCode, refusal.GetProperty("code").GetString());
        Assert.Equal(expectedPointer, refusal.GetProperty("pointer").GetString());
        Assert.Equal(missingBindingKind ? "layout-admission.layout@2.0.0" : "layout-admission.layout@" + version,
            refusal.GetProperty("target").GetString());
        if (expectIdentityUnknown)
        {
            Assert.Equal("pack.install.refused.same_version_identity_unknown",
                refusals[1].GetProperty("code").GetString());
            Assert.Equal("/", refusals[1].GetProperty("pointer").GetString());
        }
    }

    private sealed record LayoutAdmissionPreservedState(string InstalledVersions, string Overrides, string Watermarks,
        string ActivePointers, string Projections);

    private static async Task<LayoutAdmissionPreservedState> ReadLayoutAdmissionStateAsync(
        IDbContextFactory<NodeLocalPacksDbContext> factory, string tenant, HttpClient client,
        bool historicalLayout, CancellationToken deadline, bool historicalActive = false)
    {
        await using var db = await factory.CreateDbContextAsync(deadline);
        var versions = await db.InstalledVersions.AsNoTracking().Where(row => row.Tenant == tenant)
            .OrderBy(row => row.PackKey).ThenBy(row => row.Version).ToListAsync(deadline);
        var overrides = await db.Overrides.AsNoTracking().Where(row => row.Tenant == tenant)
            .OrderBy(row => row.PackKey).ThenBy(row => row.ContentKey).ToListAsync(deadline);
        var watermarks = await db.Watermarks.AsNoTracking().Where(row => row.Tenant == tenant)
            .OrderBy(row => row.PackKey).ToListAsync(deadline);
        var ownership = await db.KeyOwnership.AsNoTracking().Where(row => row.Tenant == tenant)
            .OrderBy(row => row.ContentKey).ToListAsync(deadline);
        var effective = await db.EffectiveGenerations.AsNoTracking().Where(row => row.Tenant == tenant)
            .OrderBy(row => row.Tenant).ToListAsync(deadline);
        var prepared = await db.PreparedProjections.AsNoTracking().Where(row => row.Tenant == tenant)
            .OrderBy(row => row.CandidateDigest).ToListAsync(deadline);
        var admissions = await db.ProjectionAdmissions.AsNoTracking().Where(row => row.Tenant == tenant)
            .OrderBy(row => row.AdmissionId).ToListAsync(deadline);
        Assert.Contains(versions, row => row.PackKey == "harborline.platform" && row.Version == "1.6.1"
            && row.Lifecycle == 1);
        Assert.Contains(versions, row => row.PackKey == "layout-admission.navigation" && row.Version == "1.0.0"
            && row.Lifecycle == 1);
        Assert.NotEmpty(watermarks);
        Assert.NotEmpty(ownership);
        var pointer = Assert.Single(effective);
        Assert.False(string.IsNullOrWhiteSpace(pointer.Digest));
        Assert.False(string.IsNullOrWhiteSpace(pointer.ReferencesJson));
        Assert.False(string.IsNullOrWhiteSpace(pointer.Principal));
        Assert.False(string.IsNullOrWhiteSpace(pointer.DecisionId));
        Assert.False(string.IsNullOrWhiteSpace(pointer.EvidenceIntentId));
        Assert.NotEmpty(prepared);
        Assert.Contains(admissions, row => row.PackId == "layout-admission.navigation" && row.Projected);
        Assert.DoesNotContain(admissions, row => !row.Projected);
        if (historicalLayout)
        {
            var legacy = Assert.Single(versions, row => row.PackKey == "layout-admission.historical-layout");
            Assert.Equal("1.0.0", legacy.Version);
            Assert.Equal(historicalActive ? 1 : 0, legacy.Lifecycle);
            var payload = JsonSerializer.Deserialize<Harborline.Api.Foundation.Packs.Install.InstalledPack>(legacy.PayloadJson);
            Assert.NotNull(payload);
            var layout = Assert.Single(payload.SeedItems, item => item.Key == "layout-admission.layout");
            Assert.Equal(LayoutAdmissionDefinitionJson("1.0.0", tenant), layout.CanonicalJson);
            Assert.Contains("Historical Layout declaration", layout.CanonicalJson, StringComparison.Ordinal);
            var overlay = Assert.Single(overrides, row => row.PackKey == "layout-admission.historical-layout");
            Assert.Equal("layout-admission.layout", overlay.ContentKey);
            Assert.Equal("""{"intent":"Observe"}""", overlay.OverlayJson);
            var watermark = Assert.Single(watermarks, row => row.PackKey == "layout-admission.historical-layout");
            Assert.Equal("1.0.0", watermark.Version);
            using var floorJson = JsonDocument.Parse(watermark.FloorsJson);
            Assert.Equal(3, floorJson.RootElement.GetProperty("scoring").GetInt32());
            if (historicalActive)
            {
                var active = new DurablePackInstallStore(factory).GetActive(
                    new Harborline.Foundation.Assets.Common.TenantId(tenant), "layout-admission.historical-layout");
                Assert.NotNull(active);
                Assert.Equal("1.0.0", active.Version);
                Assert.Equal(1, (int)active.Lifecycle);
                Assert.Contains(admissions, row => row.PackId == "layout-admission.historical-layout"
                    && row.PackVersion == "1.0.0" && row.Projected);
            }
        }
        using var navigation = await client.GetAsync(PackNavigationRoutes.NavigationRoute, deadline);
        Assert.Equal(HttpStatusCode.OK, navigation.StatusCode);
        var projection = await navigation.Content.ReadFromJsonAsync<PackNavigationResponseDto>(deadline);
        Assert.NotNull(projection);
        Assert.True(projection.Configured);
        Assert.NotNull(projection.Pack);
        var workspace = Assert.Single(projection.Pack.SeedWorkspaces, item => item.Id == "layout-admission-preserved-workspace");
        Assert.Equal("workspaces.layout-admission-preserved", workspace.LabelKey);
        Assert.DoesNotContain(projection.Pack.SeedWorkspaces, item => item.Id == "layout-admission-refused-workspace");
        return new(JsonSerializer.Serialize(versions), JsonSerializer.Serialize(overrides), JsonSerializer.Serialize(watermarks),
            JsonSerializer.Serialize(new { ActiveVersions = versions.Where(row => row.Lifecycle == 1).ToArray(),
                Ownership = ownership, Effective = effective }),
            JsonSerializer.Serialize(new { Prepared = prepared, Admissions = admissions, Navigation = projection }));
    }

}
