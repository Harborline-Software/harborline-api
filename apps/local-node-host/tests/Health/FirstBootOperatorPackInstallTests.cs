using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Harborline.Api.Kernel.Security.Keys;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Packs;
using Harborline.Api.LocalNodeHost.Health;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.LocalNodeHost.Tests.Authorization;

namespace Harborline.Api.LocalNodeHost.Tests.Health;

/// <summary>
/// Ticket 379 slice 1 — the first-boot operator installs and activates a pack on a CLEAN node, through
/// the real composition: the compiled entry point, the real <c>AuthorizationGate</c>, the real grant
/// store, and the genesis founder inputs <c>docs/install/first-start.md</c> names (root seed + per-boot
/// session token). No <c>TestPackGate.AllowAll()</c> and — the load-bearing part — no grant injected
/// into the store behind the route's back.
/// </summary>
/// <remarks>
/// This is deliberately distinct from
/// <see cref="Program_Composition_InstallsAndServesPackNavigationThroughTheShippingHost"/>: that proof
/// used to pre-seed an Administrator grant for the node's SIGNING KEY id, which is what hid the defect
/// the m3 exit run found (the installer re-decided about the signing key id instead of the acting
/// operator, so every first-boot install was a 500). Here the ONLY holdings are the ones the genesis
/// installer seed confers on the desktop operator party.
/// </remarks>
public sealed partial class ComposedHostBootSmokeTests
{
    [Fact]
    public async Task Program_Composition_FirstBootOperatorInstallsAndActivatesAPackOnACleanNode()
    {
        const string rootSeedHex =
            "3535353535353535353535353535353535353535353535353535353535353535";
        await using var host = ComposedHost.Start(
            "first-boot operator pack install on a clean node",
            webClient: false,
            llmProxy: false,
            schedulingDogfood: false,
            multiTeam: false,
            environment: "Production",
            rootSeedHex: rootSeedHex);
        var baseUri = await host.AwaitReadinessAsync();
        using var client = new HttpClient { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "composed-host-smoke-token");

        using var export = await client.PostAsJsonAsync(
            PackComposerRoutes.ExportRoute, FirstBootPackExportRequest(), host.Deadline);
        var packBytes = await export.Content.ReadAsByteArrayAsync(host.Deadline);
        Assert.True(export.IsSuccessStatusCode, Encoding.UTF8.GetString(packBytes));

        // 379.A1 — install and activate as the first-boot operator session. Before the fix both of these
        // were HTTP 500 (AuthorizationDeniedException out of PackInstaller.AuthorizeOrAudit).
        using var packContent = new ByteArrayContent(packBytes);
        packContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var install = await client.PostAsync(
            PackInstallRoutes.InstallRoute, packContent, host.Deadline);
        var installBody = await install.Content.ReadAsStringAsync(host.Deadline);
        Assert.Equal(HttpStatusCode.OK, install.StatusCode);
        using var installed = JsonDocument.Parse(installBody);
        Assert.True(installed.RootElement.GetProperty("installed").GetBoolean(), installBody);

        using var activate = await client.PostAsJsonAsync(
            PackInstallRoutes.ActivateRoute,
            new { packKey = FirstBootPackKey, version = "1.0.0" },
            host.Deadline);
        var activateBody = await activate.Content.ReadAsStringAsync(host.Deadline);
        Assert.Equal(HttpStatusCode.OK, activate.StatusCode);

        // 379.A2 — the transition is RECORDED against the founder's party (the desktop operator party
        // the bootstrap bearer resolves to and the genesis seed grants), never the node's signing key id.
        // The durable evidence is the gate-admitted projection admission the activate path writes with the
        // authorizing principal of the one decision it made. (The pack-install audit row itself is not yet
        // readable over HTTP on a clean node: KernelAuditPackInstallAudit writes to the in-memory
        // IAuthorizedAuditTrail while GET /audit-events reads local-node.db — ticket 331's durable-trail
        // gap, deliberately not papered over here.)
        await host.StopAsync();
        using var signer = new NodePrincipalSigner(Convert.FromHexString(rootSeedHex));
        var signingKeyId = signer.Signer.IssuerId.ToBase64Url();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSqlCipherLocalNodeDbContext(
            Convert.FromHexString(rootSeedHex),
            Path.Combine(host.DataDirectory, "local-node.db"),
            new SqlCipherKeyDerivation());
        await using var provider = services.BuildServiceProvider();
        await using var packs = await provider
            .GetRequiredService<IDbContextFactory<NodeLocalPacksDbContext>>()
            .CreateDbContextAsync();
        var principals = await packs.ProjectionAdmissions.AsNoTracking()
            .Where(row => row.PackId == FirstBootPackKey)
            .Select(row => row.Principal)
            .ToListAsync();
        Assert.NotEmpty(principals);
        Assert.DoesNotContain(signingKeyId, principals);
        Assert.All(principals, principal =>
            Assert.Equal(TestDesktopOperator.HostOperator(rootSeedHex), principal));
    }

    [Fact]
    public async Task Program_Composition_DefaultLayoutAdmissionRefusesAcrossPackSurfaces()
        => await Rev6DefaultLayoutAdmissionAsync(legacyScenario: null);

    [Theory]
    [InlineData("upgrade")]
    [InlineData("reimport")]
    [InlineData("mixed")]
    public async Task Program_Composition_LayoutRefusalPreservesHistoricalDraftState(string legacyScenario)
        => await Rev6DefaultLayoutAdmissionAsync(legacyScenario);

    private static async Task Rev6DefaultLayoutAdmissionAsync(string? legacyScenario)
    {
        const string rootSeedHex =
            "4646464646464646464646464646464646464646464646464646464646464646";
        await using var first = ComposedHost.Start(
            "REV6 default Layout admission", webClient: false, llmProxy: false,
            schedulingDogfood: false, multiTeam: false, environment: "Production", rootSeedHex: rootSeedHex);
        using var firstClient = Rev6FounderClient(await first.AwaitReadinessAsync());
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSqlCipherLocalNodeDbContext(Convert.FromHexString(rootSeedHex),
            Path.Combine(first.DataDirectory, "local-node.db"), new SqlCipherKeyDerivation());
        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<NodeLocalPacksDbContext>>();
        await Rev6InstallNavigationAsync(firstClient, first.Deadline);
        var tenant = await Rev6PrepareAndActivateAsync(firstClient, first.Deadline);
        ComposedHost? restarted = null;
        HttpClient? restartedClient = null;
        try
        {
            var client = firstClient;
            var deadline = first.Deadline;
            if (legacyScenario is not null)
            {
                await first.StopAsync();
                Rev6SeedHistoricalDraft(factory, tenant, rootSeedHex);
                restarted = ComposedHost.Start(
                    "REV6 historical Draft Layout preservation", webClient: false, llmProxy: false,
                    schedulingDogfood: false, multiTeam: false, environment: "Production",
                    rootSeedHex: rootSeedHex, dataDirectoryOverride: first.DataDirectory);
                restartedClient = Rev6FounderClient(await restarted.AwaitReadinessAsync());
                client = restartedClient;
                deadline = restarted.Deadline;
            }

            var version = legacyScenario == "reimport" ? "1.0.0" : "2.0.0";
            var packKey = legacyScenario is null ? "rev6.new-layout" : "rev6.historical-layout";
            var contents = new List<object>
            {
                new { key = "rev6.layout", kind = "Layout", version,
                    content = JsonSerializer.Deserialize<JsonElement>(Rev6LayoutContent(version, tenant)) },
            };
            if (legacyScenario == "mixed")
                contents.Add(new { key = "rev6.mixed-navigation", kind = "NavWorkspaceConfig", version,
                    content = new { seedWorkspaces = new[]
                    {
                        new { id = "rev6-refused-workspace", labelKey = "workspaces.rev6-refused" },
                    } } });
            var bytes = await Rev6ExportAsync(client, new
            {
                key = packKey, version, name = "REV6 Layout refusal", description = "Default host admission",
                scopeTier = "Vertical", contents, dependencies = Array.Empty<object>(),
            }, deadline);
            var baseline = await Rev6SnapshotAsync(factory, tenant, client, legacyScenario is not null, deadline);

            foreach (var route in new[]
                { PackInstallRoutes.PreviewRoute, PackInstallRoutes.CheckRoute, PackInstallRoutes.InstallRoute })
            {
                using var content = new ByteArrayContent(bytes);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                using var response = await client.PostAsync(route, content, deadline);
                var body = await response.Content.ReadAsStringAsync(deadline);
                Assert.Equal(route == PackInstallRoutes.InstallRoute
                    ? HttpStatusCode.UnprocessableEntity : HttpStatusCode.OK, response.StatusCode);
                using var document = JsonDocument.Parse(body);
                var result = document.RootElement;
                if (route == PackInstallRoutes.InstallRoute)
                {
                    Assert.False(result.GetProperty("installed").GetBoolean(), body);
                    Assert.Equal("install", result.GetProperty("stage").GetString());
                    Assert.Equal("Refused", result.GetProperty("preview").GetProperty("verdict").GetString());
                    Rev6AssertLayoutRefusal(result.GetProperty("preview"), version);
                }
                else
                    Assert.Equal("Refused", result.GetProperty("verdict").GetString());
                Rev6AssertLayoutRefusal(result, version);
                Assert.Equal(baseline,
                    await Rev6SnapshotAsync(factory, tenant, client, legacyScenario is not null, deadline));
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

    private static HttpClient Rev6FounderClient(Uri baseUri)
    {
        var client = new HttpClient { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "composed-host-smoke-token");
        return client;
    }

    private static async Task<byte[]> Rev6ExportAsync(HttpClient client, object request, CancellationToken deadline)
    {
        using var response = await client.PostAsJsonAsync(PackComposerRoutes.ExportRoute, request, deadline);
        var bytes = await response.Content.ReadAsByteArrayAsync(deadline);
        Assert.True(response.StatusCode == HttpStatusCode.OK, Encoding.UTF8.GetString(bytes));
        return bytes;
    }

    private static async Task Rev6InstallNavigationAsync(HttpClient client, CancellationToken deadline)
    {
        var bytes = await Rev6ExportAsync(client, new
        {
            key = "rev6.navigation", version = "1.0.0", name = "REV6 navigation control",
            description = "Layout-free install control", scopeTier = "Horizontal",
            contents = new[]
            {
                new { key = "rev6.navigation.chrome", kind = "NavWorkspaceConfig", version = "1.0.0",
                    content = new { seedWorkspaces = new[]
                    {
                        new { id = "rev6-preserved-workspace", labelKey = "workspaces.rev6-preserved" },
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
            new { packKey = "rev6.navigation", version = "1.0.0" }, deadline);
        Assert.Equal(HttpStatusCode.OK, activation.StatusCode);
        var activated = await activation.Content.ReadFromJsonAsync<ActivatePackResponseDto>(deadline);
        Assert.NotNull(activated);
        Assert.True(activated.Activated);
        Assert.Empty(activated.ProjectionRefusals);
        Assert.Empty(activated.PlatformRefusals ?? []);
    }

    private static async Task<string> Rev6PrepareAndActivateAsync(HttpClient client, CancellationToken deadline)
    {
        using var response = await client.GetAsync(ConfigurationActivationRoutes.EffectiveRoute, deadline);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var effective = await response.Content.ReadFromJsonAsync<EffectiveGenerationDto>(deadline);
        Assert.NotNull(effective);
        var references = effective.References;
        var activeKeys = references.GetProperty("activePackageKeys").EnumerateArray()
            .Select(key => key.GetString()!).ToArray();
        Assert.Contains("rev6.navigation", activeKeys);
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
                new EvidenceIntentDto(Guid.NewGuid().ToString("N"), "REV6 governed preservation baseline")), deadline);
        var activateBody = await activate.Content.ReadAsStringAsync(deadline);
        Assert.True(activate.StatusCode == HttpStatusCode.OK, activateBody);
        using var activated = JsonDocument.Parse(activateBody);
        Assert.Equal("effective", activated.RootElement.GetProperty("status").GetString());
        Assert.Equal(candidate, activated.RootElement.GetProperty("effectiveDigest").GetString());
        return references.GetProperty("tenantKey").GetString()!;
    }

    private static string Rev6LayoutContent(string version, string tenant)
        => Harborline.Blocks.BuilderDefinitions.LayoutDefinitionJson.SerializeCanonical(
            new Harborline.Blocks.BuilderDefinitions.LayoutDefinition(
                new Harborline.Blocks.BuilderDefinitions.LayoutDefinitionEnvelope(
                    "rev6.layout", version, tenant,
                    Harborline.Blocks.BuilderDefinitions.LayoutCascadeLayer.TenantConfiguration,
                    JsonSerializer.SerializeToElement(new { source = "rev6-historical-layout" }), "standard", false,
                    [new Harborline.Blocks.BuilderDefinitions.LayoutDefinitionRequirement("platform.layout", "1.0.0")],
                    new Harborline.Foundation.Definitions.DefinitionContractVersion(1, 0)),
                1, Harborline.Blocks.BuilderDefinitions.LayoutMedium.Screen,
                Harborline.Blocks.BuilderDefinitions.LayoutIntent.Observe,
                [new Harborline.Blocks.BuilderDefinitions.LayoutBlock("title", "layout.text",
                    new Harborline.Blocks.BuilderDefinitions.LayoutStaticBinding(
                        JsonSerializer.SerializeToElement("REV6 historical Layout")), [])],
                [], [], [], null, []));

    private static void Rev6SeedHistoricalDraft(IDbContextFactory<NodeLocalPacksDbContext> factory,
        string tenant, string rootSeedHex)
    {
        using var signer = new NodePrincipalSigner(Convert.FromHexString(rootSeedHex));
        var floors = new Dictionary<string, int>(StringComparer.Ordinal) { ["scoring"] = 3 };
        Harborline.Api.Foundation.Packs.Install.PackSeedItem Seed(string key,
            Harborline.Api.Foundation.Packs.Model.PackContentKind kind, string json)
            => new(key, kind, "1.0.0", json,
                Harborline.Api.Foundation.Blobs.Cid.FromBytes(Encoding.UTF8.GetBytes(json)));
        var pack = new Harborline.Api.Foundation.Packs.Install.InstalledPack(
            "rev6.historical-layout", "1.0.0", Harborline.Api.Foundation.Packs.Model.PackScopeTier.Vertical,
            Harborline.Api.Foundation.Packs.Install.PackLifecycleState.Draft,
            [Seed("rev6.layout", Harborline.Api.Foundation.Packs.Model.PackContentKind.Layout,
                Rev6LayoutContent("1.0.0", tenant))],
            floors, new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), signer.Signer.IssuerId,
            1, Harborline.Api.Foundation.Packs.Trust.TrustScope.OwnRoster, []);
        new DurablePackInstallStore(factory).Commit(new(
            new Harborline.Api.Foundation.Assets.Common.TenantId(tenant), pack,
            new Harborline.Api.Foundation.Packs.Install.PackInstallWatermark(pack.PackKey, "1.0.0", floors),
            [new Harborline.Api.Foundation.Packs.Install.PackTenantOverride("rev6.layout",
                System.Text.Json.Nodes.JsonNode.Parse("""{"intent":"Observe"}""")!)]));
    }

    private static void Rev6AssertLayoutRefusal(JsonElement result, string version)
    {
        var code = Assert.Single(result.GetProperty("refusalCodes").EnumerateArray());
        Assert.Equal("layout.pack.capability_unsupported", code.GetString());
        var refusal = Assert.Single(result.GetProperty("refusals").EnumerateArray());
        Assert.Equal("layout.pack.capability_unsupported", refusal.GetProperty("code").GetString());
        Assert.Equal("/envelope/requires/0/minimum_platform_version", refusal.GetProperty("pointer").GetString());
        Assert.Equal("rev6.layout@" + version, refusal.GetProperty("target").GetString());
    }

    private sealed record Rev6PreservedState(string InstalledVersions, string Overrides, string Watermarks,
        string ActivePointers, string Projections);

    private static async Task<Rev6PreservedState> Rev6SnapshotAsync(
        IDbContextFactory<NodeLocalPacksDbContext> factory, string tenant, HttpClient client,
        bool historicalDraft, CancellationToken deadline)
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
        Assert.Contains(versions, row => row.PackKey == "rev6.navigation" && row.Version == "1.0.0"
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
        Assert.Contains(admissions, row => row.PackId == "rev6.navigation" && row.Projected);
        Assert.DoesNotContain(admissions, row => !row.Projected);
        if (historicalDraft)
        {
            var legacy = Assert.Single(versions.Where(row => row.PackKey == "rev6.historical-layout"));
            Assert.Equal("1.0.0", legacy.Version);
            Assert.Equal(0, legacy.Lifecycle);
            var payload = JsonSerializer.Deserialize<Harborline.Api.Foundation.Packs.Install.InstalledPack>(legacy.PayloadJson);
            Assert.NotNull(payload);
            var layout = Assert.Single(payload.SeedItems.Where(item => item.Key == "rev6.layout"));
            Assert.Equal(Rev6LayoutContent("1.0.0", tenant), layout.CanonicalJson);
            Assert.Contains("REV6 historical Layout", layout.CanonicalJson, StringComparison.Ordinal);
            var overlay = Assert.Single(overrides.Where(row => row.PackKey == "rev6.historical-layout"));
            Assert.Equal("rev6.layout", overlay.ContentKey);
            Assert.Equal("""{"intent":"Observe"}""", overlay.OverlayJson);
            var watermark = Assert.Single(watermarks.Where(row => row.PackKey == "rev6.historical-layout"));
            Assert.Equal("1.0.0", watermark.Version);
            using var floorJson = JsonDocument.Parse(watermark.FloorsJson);
            Assert.Equal(3, floorJson.RootElement.GetProperty("scoring").GetInt32());
        }
        using var navigation = await client.GetAsync(PackNavigationRoutes.NavigationRoute, deadline);
        Assert.Equal(HttpStatusCode.OK, navigation.StatusCode);
        var projection = await navigation.Content.ReadFromJsonAsync<PackNavigationResponseDto>(deadline);
        Assert.NotNull(projection);
        Assert.True(projection.Configured);
        Assert.NotNull(projection.Pack);
        var workspace = Assert.Single(projection.Pack.SeedWorkspaces.Where(item => item.Id == "rev6-preserved-workspace"));
        Assert.Equal("workspaces.rev6-preserved", workspace.LabelKey);
        Assert.DoesNotContain(projection.Pack.SeedWorkspaces, item => item.Id == "rev6-refused-workspace");
        return new(JsonSerializer.Serialize(versions), JsonSerializer.Serialize(overrides), JsonSerializer.Serialize(watermarks),
            JsonSerializer.Serialize(new { ActiveVersions = versions.Where(row => row.Lifecycle == 1).ToArray(),
                Ownership = ownership, Effective = effective }),
            JsonSerializer.Serialize(new { Prepared = prepared, Admissions = admissions, Navigation = projection }));
    }

    private const string FirstBootPackKey = "ticket379.first-boot";

    private static object FirstBootPackExportRequest() => new
    {
        key = FirstBootPackKey,
        version = "1.0.0",
        name = "Ticket 379 first boot",
        description = "first-boot operator install on a clean node",
        scopeTier = "Horizontal",
        contents = new[]
        {
            new
            {
                key = "fixtures.chrome",
                kind = "NavWorkspaceConfig",
                version = "1.0.0",
                content = new
                {
                    seedWorkspaces = new[]
                    {
                        new { id = "ticket-379-workspace", labelKey = "workspaces.ticket-379" },
                    },
                },
            },
        },
        dependencies = Array.Empty<object>(),
    };
}
