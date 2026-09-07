using System.Net;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Blocks.Assets.Registry.DependencyInjection;
using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Blobs;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Packs.Dcp;
using Harborline.Api.Foundation.Packs.Export;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Admission;
using Harborline.Api.Foundation.Packs.Install.Audit;
using Harborline.Api.Foundation.Packs.Install.Trust;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Navigation;
using Harborline.Api.Foundation.Packs.Serialization;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Foundation.Packs.Validation;
using Harborline.Api.Foundation.Packs.Verify;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.PackProjection;
using Harborline.Api.LocalNodeHost.Health;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

public sealed class UnsupportedPackContentKindInstallTests
{
    private static readonly TenantId Tenant = new("aaaaaaaa-0000-0000-0000-000000000015");
    private static readonly DateTimeOffset Now = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Install_refuses_standards_catalog_with_a_stated_reason()
    {
        var (installer, context, store, packBytes, keyPair) =
            await CreateFixtureAsync(PackContentKind.StandardsCatalog);
        using (keyPair)
        {
            var outcome = installer.Install(packBytes, context);

            Assert.False(outcome.Installed);
            Assert.Equal(PackInstallVerdict.Refused, outcome.Preview.Verdict);
            Assert.Contains(
                "pack.install.refused.unsupported_content_kind.standards_catalog",
                outcome.RefusalCodes);
            Assert.Empty(store.ListInstalled(Tenant));
        }
    }

    [Fact]
    public async Task Install_activates_and_projects_nav_workspace_config()
    {
        var navContent = JsonNode.Parse(
            """
            {
              "seedWorkspaces": [
                {
                  "id": "operations",
                  "labelKey": "workspaces.operations",
                  "groups": [
                    {
                      "id": "primary",
                      "labelKey": "workspaceGroups.operations.primary",
                      "itemIds": ["assets"]
                    }
                  ]
                }
              ]
            }
            """)!;
        var (installer, context, store, packBytes, keyPair) =
            await CreateFixtureAsync(PackContentKind.NavWorkspaceConfig, navContent);
        using (keyPair)
        {
            var outcome = installer.Install(packBytes, context);

            Assert.True(outcome.Installed);
            Assert.Equal(PackInstallVerdict.WouldInstall, outcome.Preview.Verdict);
            var activation = installer.Activate(Tenant, outcome.PackKey, outcome.Version, Now, "test-operator");
            Assert.True(activation.Activated);

            using var response = await GetNavigationAsync(store);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonObject>();
            Assert.True(json!["configured"]!.GetValue<bool>());
            Assert.Equal(
                "operations",
                json["pack"]!["seedWorkspaces"]![0]!["id"]!.GetValue<string>());
        }
    }

    [Theory]
    [InlineData("bodyTemplate", "Fifth", PackNavigationAdmissionCodes.UnknownBodyTemplate)]
    [InlineData("bodyTemplate", "Feed", PackNavigationAdmissionCodes.TraitTemplateMismatch)]
    public async Task Install_refuses_invalid_panel_shapes_before_seed_commit(
        string property,
        string value,
        string expectedCode)
    {
        var navContent = JsonNode.Parse(
            $$"""
            {
              "seedWorkspaces": [{ "id": "operations", "labelKey": "workspaces.operations" }],
              "panelSet": [{
                "id": "notes",
                "binding": "panels.notes.toggle",
                "shortcut": "mod+shift+n",
                "defaultWidth": 360,
                "minimumHeight": 180,
                "defaultOpen": false,
                "labelKey": "panels.notes",
                "headerForm": "Title",
                "{{property}}": "{{value}}",
                "footer": { "kind": "Claim", "labelKey": "panels.notes.footer" },
                "traits": ["Scoped"]
              }]
            }
            """)!;
        var (installer, context, store, packBytes, keyPair) =
            await CreateFixtureAsync(PackContentKind.NavWorkspaceConfig, navContent);
        using (keyPair)
        {
            var outcome = installer.Install(packBytes, context);

            Assert.False(outcome.Installed);
            Assert.Equal(PackInstallVerdict.Refused, outcome.Preview.Verdict);
            Assert.Contains(PackInstallCodes.RefusedAdmission, outcome.RefusalCodes);
            Assert.Equal(expectedCode, Assert.Single(outcome.Preview.AdmissionRefusals).Code);
            Assert.Empty(store.ListInstalled(Tenant));
        }
    }

    [Fact]
    public async Task Install_refuses_navigation_collisions_between_pack_contributions()
    {
        var first = JsonNode.Parse(
            """{ "seedWorkspaces": [{ "id": "operations", "labelKey": "workspaces.operations" }] }""")!;
        var second = JsonNode.Parse(
            """{ "seedWorkspaces": [{ "id": "operations", "labelKey": "workspaces.operations.other" }] }""")!;
        var (installer, context, store, packBytes, keyPair) =
            await CreateFixtureAsync(PackContentKind.NavWorkspaceConfig, first, second);
        using (keyPair)
        {
            var outcome = installer.Install(packBytes, context);

            Assert.False(outcome.Installed);
            Assert.Equal(PackInstallVerdict.Refused, outcome.Preview.Verdict);
            Assert.Equal(
                PackNavigationAdmissionCodes.DuplicateWorkspace,
                Assert.Single(outcome.Preview.AdmissionRefusals).Code);
            Assert.Empty(store.ListInstalled(Tenant));
        }
    }

    [Fact]
    public async Task Install_refuses_an_oversized_valid_navigation_declaration_before_commit()
    {
        var navContent = OversizedNavigationContent();
        var canonicalJson = navContent.ToJsonString();
        var item = new PackComposedItem(
            "test.oversized-navigation",
            "content",
            PackContentKind.NavWorkspaceConfig,
            "1.0.0",
            canonicalJson);

        Assert.True(PackNavigationDeclarationParser.DeclaresNavigation(canonicalJson));
        Assert.Equal(
            PackNavigationAdmissionCodes.BoundsExceeded,
            Assert.Single(PackNavigationContentAdmission.Validate([item], Tenant)).Code);

        var (installer, context, store, packBytes, keyPair) =
            await CreateFixtureAsync(PackContentKind.NavWorkspaceConfig, navContent);
        using (keyPair)
        {
            var outcome = installer.Install(packBytes, context);

            Assert.False(outcome.Installed);
            Assert.Equal(PackInstallVerdict.Refused, outcome.Preview.Verdict);
            Assert.Equal(
                PackNavigationAdmissionCodes.BoundsExceeded,
                Assert.Single(outcome.Preview.AdmissionRefusals).Code);
            Assert.Empty(store.ListInstalled(Tenant));
        }
    }

    [Theory]
    [InlineData("defaultWidth", "360")]
    [InlineData("minimumHeight", "180")]
    public async Task Install_refuses_string_valued_panel_sizes_as_malformed_without_throwing(
        string property,
        string value)
    {
        var navContent = JsonNode.Parse(
            """
            {
              "seedWorkspaces": [{ "id": "operations", "labelKey": "workspaces.operations" }],
              "panelSet": [{
                "id": "notes",
                "binding": "panels.notes.toggle",
                "shortcut": "mod+shift+n",
                "defaultWidth": 360,
                "minimumHeight": 180,
                "defaultOpen": false,
                "labelKey": "panels.notes",
                "headerForm": "Title",
                "bodyTemplate": "Fields",
                "footer": { "kind": "Claim", "labelKey": "panels.notes.footer" },
                "traits": ["Scoped"]
              }]
            }
            """)!;
        navContent["panelSet"]![0]![property] = value;
        var (installer, context, store, packBytes, keyPair) =
            await CreateFixtureAsync(PackContentKind.NavWorkspaceConfig, navContent);
        using (keyPair)
        {
            var outcome = installer.Install(packBytes, context);

            Assert.False(outcome.Installed);
            Assert.Equal(PackInstallVerdict.Refused, outcome.Preview.Verdict);
            Assert.Equal(
                PackNavigationAdmissionCodes.Malformed,
                Assert.Single(outcome.Preview.AdmissionRefusals).Code);
            Assert.Empty(store.ListInstalled(Tenant));
        }
    }

    [Fact]
    public async Task Install_refuses_documents_panel_when_any_composed_workspace_lacks_a_spine()
    {
        var documents = JsonNode.Parse(
            """
            {
              "seedWorkspaces": [{
                "id": "documents",
                "labelKey": "workspaces.documents",
                "documentSpine": [{
                  "id": "root",
                  "labelKey": "documents.root",
                  "binding": "documents.root"
                }]
              }],
              "panelSet": [{
                "id": "documents",
                "binding": "panels.documents.toggle",
                "shortcut": "mod+shift+d",
                "defaultWidth": 420,
                "minimumHeight": 220,
                "defaultOpen": false
              }]
            }
            """)!;
        var unspined = JsonNode.Parse(
            """{ "seedWorkspaces": [{ "id": "operations", "labelKey": "workspaces.operations" }] }""")!;
        var (installer, context, store, packBytes, keyPair) =
            await CreateFixtureAsync(PackContentKind.NavWorkspaceConfig, documents, unspined);
        using (keyPair)
        {
            var outcome = installer.Install(packBytes, context);

            Assert.False(outcome.Installed);
            Assert.Equal(PackInstallVerdict.Refused, outcome.Preview.Verdict);
            Assert.Equal(
                PackNavigationAdmissionCodes.DanglingReference,
                Assert.Single(outcome.Preview.AdmissionRefusals).Code);
            Assert.Empty(store.ListInstalled(Tenant));
        }
    }

    [Fact]
    public async Task Install_refuses_cascade_defaults_with_a_stated_reason()
    {
        var (installer, context, store, packBytes, keyPair) =
            await CreateFixtureAsync(PackContentKind.CascadeDefaults);
        using (keyPair)
        {
            var outcome = installer.Install(packBytes, context);

            Assert.False(outcome.Installed);
            Assert.Equal(PackInstallVerdict.Refused, outcome.Preview.Verdict);
            Assert.Contains(
                "pack.install.refused.unsupported_content_kind.cascade_defaults",
                outcome.RefusalCodes);
            Assert.Empty(store.ListInstalled(Tenant));
        }
    }

    [Fact]
    public async Task Install_refuses_terminology_override_with_a_stated_reason()
    {
        var (installer, context, store, packBytes, keyPair) =
            await CreateFixtureAsync(PackContentKind.TerminologyOverride);
        using (keyPair)
        {
            var outcome = installer.Install(packBytes, context);

            Assert.False(outcome.Installed);
            Assert.Equal(PackInstallVerdict.Refused, outcome.Preview.Verdict);
            Assert.Contains(
                "pack.install.refused.unsupported_content_kind.terminology_override",
                outcome.RefusalCodes);
            Assert.Empty(store.ListInstalled(Tenant));
        }
    }

    [Fact]
    public async Task Projector_loudly_refuses_an_unsupported_legacy_seed()
    {
        using var keyPair = KeyPair.Generate();
        var store = new InMemoryPackInstallStore();
        var json = new JsonObject().ToJsonString();
        var seed = new PackSeedItem(
            "legacy-content",
            PackContentKind.TerminologyOverride,
            "1.0.0",
            json,
            Cid.FromBytes(System.Text.Encoding.UTF8.GetBytes(json)));
        var pack = new InstalledPack(
            "legacy.unsupported",
            "1.0.0",
            PackScopeTier.Vertical,
            PackLifecycleState.Draft,
            [seed],
            new Dictionary<string, int>(),
            Now,
            keyPair.PrincipalId,
            1,
            TrustScope.OwnRoster,
            Array.Empty<PackDependencyRef>());
        store.Commit(new PackInstallTransaction(
            Tenant,
            pack,
            new PackInstallWatermark(pack.PackKey, pack.Version, new Dictionary<string, int>()),
            Array.Empty<PackTenantOverride>()));
        store.Activate(Tenant, pack.PackKey, pack.Version);
        using var services = new ServiceCollection()
            .AddLogging()
            .AddInMemoryAssetTypeSystem()
            .BuildServiceProvider();
        IPackSeedProjector projector = new PackSeedProjector(
            store,
            services.GetRequiredService<IEntityTypeRegistry>(),
            NullLogger<PackSeedProjector>.Instance, time: TimeProvider.System);

        var summary = await projector.ProjectActivePacksAsync(Tenant);

        var refusal = Assert.Single(summary.Refusals);
        Assert.Equal(PackContentKind.TerminologyOverride, refusal.ContentKind);
        Assert.Equal("pack.projection.unsupported_content_kind", refusal.Code);
        Assert.Equal(0, summary.OtherKindsSkipped);
    }

    private static async Task<(
        IPackInstaller Installer,
        PackInstallContext Context,
        InMemoryPackInstallStore Store,
        byte[] PackBytes,
        KeyPair KeyPair)> CreateFixtureAsync(
        PackContentKind kind,
        JsonNode? content = null,
        JsonNode? secondContent = null)
    {
        var keyPair = KeyPair.Generate();
        var signer = new Ed25519Signer(keyPair);
        var codec = new PackFileCodec();
        var exporter = new PackExporter(
            new PackContentCanonicalizer(),
            new PackDcpCanonicalizer(),
            new PackValidator(new PackContentPiiScanner()),
            new DcpValidator(DcpCounselRegister.FromEmbeddedResource()),
            codec, timeProvider: TimeProvider.System);
        var sources = new List<PackContentSource>
        {
            new("content", kind, "1.0.0", content ?? new JsonObject()),
        };
        if (secondContent is not null)
            sources.Add(new PackContentSource("content-two", kind, "1.0.0", secondContent));
        var export = await exporter.ExportAsync(
            new PackExportRequest(
                Key: $"test.{kind.ToString().ToLowerInvariant()}",
                Version: "1.0.0",
                Name: $"{kind} test pack",
                Description: "Exercises install refusal for content without a projector.",
                ScopeTier: PackScopeTier.Vertical,
                Contents: sources,
                Dependencies: Array.Empty<PackDependencyRef>(),
                CapabilityRequirements: Array.Empty<string>(),
                Epoch: 1,
                Dcp: DomainComplianceProfile.General("test-author")),
            signer);
        Assert.True(export.Succeeded, string.Join(
            "; ", export.Validation.Errors.Select(error => $"{error.Code}: {error.Message}")));

        var trustStore = new InMemoryPackTrustStore(
        [
            new PackTrustRoot(TrustScope.OwnRoster, keyPair.PrincipalId, 1, TrustRootStatus.Current),
        ]);
        var store = new InMemoryPackInstallStore();
        var installer = new PackInstaller(
            new PackVerifier(new Ed25519Verifier(), codec),
            store,
            new WorkflowRefusingPackContentAdmission(),
            new InMemoryPackInstallAudit(),
            Harborline.Api.LocalNodeHost.Tests.Authorization.TestAuthorization.AllowGate());
        var context = new PackInstallContext(
            Tenant, trustStore, PackRevocationList.Empty, Now, TimeSpan.FromDays(30), Principal: "test-operator");

        return (installer, context, store, export.FileBytes!, keyPair);
    }

    private static JsonNode OversizedNavigationContent()
    {
        var workspaces = new JsonArray();
        for (var workspaceIndex = 0; workspaceIndex < 32; workspaceIndex++)
        {
            var groups = new JsonArray();
            for (var groupIndex = 0; groupIndex < 16; groupIndex++)
            {
                var itemIds = new JsonArray();
                for (var itemIndex = 0; itemIndex < 64; itemIndex++)
                    itemIds.Add($"item-{workspaceIndex:D2}-{groupIndex:D2}-{itemIndex:D2}");
                groups.Add(new JsonObject
                {
                    ["id"] = $"group-{groupIndex:D2}",
                    ["labelKey"] = $"workspaceGroups.w{workspaceIndex:D2}.g{groupIndex:D2}",
                    ["itemIds"] = itemIds,
                });
            }
            workspaces.Add(new JsonObject
            {
                ["id"] = $"workspace-{workspaceIndex:D2}",
                ["labelKey"] = $"workspaces.w{workspaceIndex:D2}",
                ["groups"] = groups,
            });
        }

        var content = new JsonObject { ["seedWorkspaces"] = workspaces };
        Assert.True(content.ToJsonString().Length > 262_144);
        return content;
    }

    private static async Task<HttpResponseMessage> GetNavigationAsync(IPackInstallStore store)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var activeTeam = new FixedActiveTeamAccessor(new TeamContext(
            new TeamId(Guid.Parse(Tenant.Value)),
            "Unsupported content test",
            new ServiceCollection().BuildServiceProvider(), TimeProvider.System));
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

    private sealed class FixedActiveTeamAccessor(TeamContext active) : IActiveTeamAccessor
    {
        public TeamContext? Active { get; } = active;
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged;
        private void KeepEvent() => ActiveChanged?.Invoke(this, new ActiveTeamChangedEventArgs(null, null));
    }
}
