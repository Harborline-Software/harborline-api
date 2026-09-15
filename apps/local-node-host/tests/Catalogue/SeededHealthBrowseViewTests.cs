using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Blocks.Assets.Registry.DependencyInjection;
using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Blobs;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Foundation.ViewDefinitions;
using Harborline.Api.LocalNodeHost.Data.PackProjection;
using Harborline.Api.LocalNodeHost.Health;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Catalogue;

public sealed class SeededHealthBrowseViewTests
{
    private const string PackResource = "Harborline.Api.LocalNodeHost.Packs.platform-pack.export.json";
    private const string PackVersion = "1.2.0";
    private const string DefinitionVersion = "1.0.0";
    private const string GridKind = "views.entity-list/grid";

    private static readonly SurfaceExpectation[] ExpectedSurfaces =
    [
        new("asset-types", "AssetTypeDefinition", ["id", "title", "version", "status"]),
        new("forms", "FormDefinition", ["formId", "title", "version", "cascadeLayer"]),
        new("workflows", "WorkflowDefinition", ["id", "title", "version", "status"]),
        new("standards", "StandardsCatalog", ["id", "title", "version", "status"]),
        new("defaults", "CascadeDefaults", ["id", "title", "version", "status"]),
        new("terminology", "TerminologyOverride", ["id", "title", "version", "status"]),
        new("documents", "TemplateDefinition", ["id", "title", "version", "status"]),
        new("taxonomies", "TaxonomyDefinition", ["id", "title", "version", "status"]),
        new("reports", "ReportDefinition", ["id", "title", "version", "status"]),
        new("data-exchanges", "DataExchangeDefinition", ["id", "title", "version", "status"]),
        new("standing-rules", "StandingRuleDefinition", ["id", "title", "version", "status"]),
        new("schedules", "ScheduleDefinition", ["id", "title", "version", "status"]),
        new("views", "ViewDefinition", ["id", "title", "version", "status"]),
    ];

    private static readonly string[] ExpectedFormActionIds =
        ["author", "export", "verify", "check", "install", "activate", "create", "read"];

    private static readonly string[] ExpectedFormActionOperations =
        ["pack.validate", "pack.export", "pack.verify", "pack.check", "pack.install", "pack.activate", "record.create", "record.read"];

    [Fact]
    public void Platform_export_carries_the_exact_frozen_health_and_browse_sets()
    {
        using var document = ReadPack();
        var definitions = ViewItems(document)
            .ToDictionary(item => item.GetProperty("key").GetString()!, StringComparer.Ordinal);
        var expectedHealth = ExpectedSurfaces.Select(surface => $"platform.health.{surface.PrimitiveId}").ToArray();
        var expectedBrowse = ExpectedSurfaces.Select(surface => $"platform.browse.{surface.PrimitiveId}").ToArray();

        Assert.Equal(
            expectedHealth.Order(StringComparer.Ordinal),
            definitions.Keys.Where(key => key.StartsWith("platform.health.", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
        Assert.Equal(
            expectedBrowse.Order(StringComparer.Ordinal),
            definitions.Keys.Where(key => key.StartsWith("platform.browse.", StringComparison.Ordinal)).Order(StringComparer.Ordinal));

        foreach (var surface in ExpectedSurfaces)
        {
            AssertDefinition(definitions[$"platform.health.{surface.PrimitiveId}"], surface, expectedActions: false);
            AssertDefinition(
                definitions[$"platform.browse.{surface.PrimitiveId}"],
                surface,
                expectedActions: surface.PrimitiveId == "forms");
        }
    }

    [Fact]
    public void Every_frozen_health_and_browse_definition_compiles_to_its_declared_grid_projection()
    {
        using var document = ReadPack();
        var items = FrozenViewItems(document).Select(ToSeedItem).ToArray();

        Assert.Equal(26, items.Length);
        foreach (var item in items)
        {
            Assert.True(
                RenderPlanCompiler.TryCompile(item, "harborline.platform", PackVersion, out var plan, out var refusalCode),
                $"{item.Key}: {refusalCode}");
            Assert.NotNull(plan);
            Assert.Equal(item.Key, plan.DefinitionId);
            Assert.Equal(DefinitionVersion, plan.DefinitionVersion);
            Assert.Equal("harborline.platform", plan.PackKey);
            Assert.Equal(GridKind, plan.Bindings.GetProperty("viewKind").GetString());
            using var contentDocument = JsonDocument.Parse(item.CanonicalJson);
            Assert.True(JsonNode.DeepEquals(
                JsonNode.Parse(contentDocument.RootElement.GetProperty("parameters").GetRawText()),
                JsonNode.Parse(plan.Bindings.GetProperty("parameters").GetRawText())));
        }
    }

    [Fact]
    public async Task Frozen_health_and_browse_definitions_project_to_the_registry_and_render_plan_catalogue()
    {
        var tenant = new TenantId("aaaaaaaa-0000-0000-0000-000000000426");
        using var document = ReadPack();
        var items = FrozenViewItems(document).Select(ToSeedItem).ToArray();
        Assert.Equal(26, items.Length);
        var store = new InMemoryPackInstallStore();
        using var keyPair = KeyPair.Generate();
        var pack = new InstalledPack(
            "seeded-health-browse.test",
            PackVersion,
            PackScopeTier.Horizontal,
            PackLifecycleState.Draft,
            items,
            new Dictionary<string, int>(),
            new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero),
            keyPair.PrincipalId,
            1,
            TrustScope.OwnRoster,
            Array.Empty<PackDependencyRef>());
        store.Commit(new PackInstallTransaction(
            tenant,
            pack,
            new PackInstallWatermark(pack.PackKey, pack.Version, pack.SafetyFloors),
            Array.Empty<PackTenantOverride>()));
        store.Activate(tenant, pack.PackKey, pack.Version);

        var definitions = new InMemoryViewDefinitionRegistry(new AcceptAllDescriptorRegistry());
        var renderPlans = new InMemoryRenderPlanCatalogue();
        using var services = new ServiceCollection()
            .AddLogging()
            .AddInMemoryAssetTypeSystem()
            .BuildServiceProvider();
        IPackSeedProjector projector = new PackSeedProjector(
            store,
            services.GetRequiredService<IEntityTypeRegistry>(),
            NullLogger<PackSeedProjector>.Instance,
            time: TimeProvider.System,
            viewDefinitions: definitions,
            renderPlans: renderPlans);

        var summary = await projector.ProjectActivePacksAsync(tenant);
        var projected = await definitions.ListDefinitionsAsync(tenant.Value, CancellationToken.None);

        Assert.Empty(summary.Refusals);
        Assert.Equal(
            items.Select(item => item.Key).Order(StringComparer.Ordinal),
            projected.Select(definition => definition.Key).Order(StringComparer.Ordinal));
        Assert.All(items, item => Assert.NotNull(
            renderPlans.Get(tenant, PackContentKind.ViewDefinition, item.Key, item.Version)));
    }

    private static void AssertDefinition(
        JsonElement item,
        SurfaceExpectation expected,
        bool expectedActions)
    {
        var outerKey = item.GetProperty("key").GetString();
        var content = item.GetProperty("content");
        var parameters = content.GetProperty("parameters");

        Assert.Equal("ViewDefinition", item.GetProperty("kind").GetString());
        Assert.Equal(DefinitionVersion, item.GetProperty("version").GetString());
        Assert.Equal(outerKey, content.GetProperty("key").GetString());
        Assert.Equal(DefinitionVersion, content.GetProperty("version").GetString());
        Assert.Equal("bootstrap", content.GetProperty("tenant").GetString());
        Assert.Equal(1, content.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(GridKind, content.GetProperty("viewKind").GetString());
        Assert.Equal(expected.EntityType, parameters.GetProperty("entityType").GetString());
        Assert.Equal(
            expected.FieldIds,
            parameters.GetProperty("fields").EnumerateArray()
                .Select(field => field.GetProperty("id").GetString()));

        if (expectedActions)
        {
            var actions = parameters.GetProperty("actions").EnumerateArray().ToArray();
            Assert.Equal(ExpectedFormActionIds, actions.Select(action => action.GetProperty("id").GetString()));
            Assert.Equal(ExpectedFormActionOperations, actions.Select(action => action.GetProperty("operation").GetString()));
        }
        else
        {
            Assert.False(parameters.TryGetProperty("actions", out _));
        }
    }

    private static JsonDocument ReadPack()
    {
        using var stream = typeof(PlatformPackPreloadHostedService).Assembly.GetManifestResourceStream(PackResource)!;
        return JsonDocument.Parse(stream);
    }

    private static IEnumerable<JsonElement> ViewItems(JsonDocument document) =>
        document.RootElement.GetProperty("contents").EnumerateArray()
            .Where(item => item.GetProperty("kind").GetString() == "ViewDefinition");

    private static IEnumerable<JsonElement> FrozenViewItems(JsonDocument document) => ViewItems(document)
        .Where(item =>
        {
            var key = item.GetProperty("key").GetString()!;
            return key.StartsWith("platform.health.", StringComparison.Ordinal)
                   || key.StartsWith("platform.browse.", StringComparison.Ordinal);
        });

    private static PackSeedItem ToSeedItem(JsonElement item)
    {
        var content = item.GetProperty("content").GetRawText();
        return new PackSeedItem(
            item.GetProperty("key").GetString()!,
            PackContentKind.ViewDefinition,
            item.GetProperty("version").GetString()!,
            content,
            Cid.FromBytes(Encoding.UTF8.GetBytes(content)));
    }

    private sealed record SurfaceExpectation(string PrimitiveId, string EntityType, string[] FieldIds);

    private sealed class AcceptAllDescriptorRegistry : IViewDefinitionDescriptorRegistry
    {
        public ValueTask AdmitAsync(ViewDefinition definition, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }
}
