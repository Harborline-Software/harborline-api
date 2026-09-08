using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Harborline.Api.Blocks.Assets.Registry.Catalogs;
using Harborline.Api.Blocks.Assets.Registry.Model;
using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.Forms.Engine;
using Harborline.Api.Foundation.Forms.Engine.Capabilities;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.Kernel.Schema;
using Harborline.Api.LocalNodeHost.Data.AssetRegistry;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Forms;
using Harborline.Api.LocalNodeHost.Health;

using ActorId = Harborline.Api.Foundation.Assets.Common.ActorId;
using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.AssetRegistry;

/// <summary>
/// ADR 0101 Rev 3.1 Wave 3a — the residential living-standard CATALOG goes live end-to-end. Route-level
/// e2e over a real in-process Kestrel listener using the SAME production composition as the node, seeded by
/// the SAME <see cref="LivingStandardCatalogDevSeeder.SeedAsync"/> path the dev node runs. The crown test
/// proves: submitting the seeded catalog form through the real forms route projects a typed condition
/// assessment PER RATED ITEM onto the inspected unit ("one act, many artifacts"), readable back over the
/// condition-history route with provenance to the form + the item field.
/// </summary>
public sealed class LivingStandardCatalogRouteTests : IAsyncLifetime
{
    private static readonly TeamId TeamA = new(Guid.Parse("aaaa0000-0000-0000-0000-0000000ca710"));
    private static readonly ActorId Operator = new("local");

    private const string AssetBase = "/api/local-node/asset-registry";
    private const string FormsBase = "/api/local-node/forms";
    private const string UnitType = "residential-unit";

    private static readonly IReadOnlyList<string> OperatorRoles = new[] { FormsRoutes.NodeOperatorRole };

    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development",
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        builder.Services.AddSingleton<Harborline.Api.Foundation.Recovery.TenantKey.ITenantKeyProvider,
            Harborline.Api.Foundation.Recovery.TenantKey.InMemoryTenantKeyProvider>();
        builder.Services.AddSingleton<Harborline.Api.Foundation.Recovery.Crypto.IFieldEncryptor,
            Harborline.Api.Foundation.Recovery.Crypto.TenantKeyProviderFieldEncryptor>();

        builder.Services.AddTestAuthorizationGate().AddTestNodeForms();
        builder.Services.AddNodeAssetRegistry();

        _app = builder.Build();

        var tenant = ActiveTeamTenantContext.ProjectTenantId(TeamA);

        // Seed the catalog exactly as the dev node would (form + bindings + shared seed).
        await LivingStandardCatalogDevSeeder.SeedAsync(
            _app.Services.GetRequiredService<ISchemaRegistry>(),
            _app.Services.GetRequiredService<AuthorizedFormDefinitionLifecycle>(),
            _app.Services.GetRequiredService<IConditionRatingFieldBindingStore>(),
            _app.Services.GetRequiredService<IStandardCatalogSeedStore>(),
            tenant,
            DateTimeOffset.UtcNow);

        // A unit type so a unit entity can be created and inspected.
        _app.Services.GetRequiredService<IEntityTypeRegistry>().SeedType(new EntityTypeSeed(
            new EntityTypeId(UnitType),
            new EntityTypeDescriptor("Residential unit", EntityTrait.Container | EntityTrait.Maintainable),
            CascadeLayer.Pack));

        var activeTeam = new SingleTeamAccessor(new TeamContext(TeamA, "Team A", new ServiceCollection().BuildServiceProvider(), TimeProvider.System));

        _app.Use(async (http, next) =>
        {
            http.Features.Set(DesktopPlaneRequestFeature.Instance);
            await next(http);
        });
        AssetRegistryRoutes.Map(
            _app.MapDeviceReachableProductDataGroup(),
            _app.Services.GetRequiredService<IEntityTypeRegistry>(),
            _app.Services.GetRequiredService<IRegistryEntityRepository>(),
            _app.Services.GetRequiredService<ITypedRelationshipStore>(),
            _app.Services.GetRequiredService<IConditionAssessmentStore>(),
            _app.Services.GetRequiredService<IFormSubmissionRecordStore>(),
            activeTeam,
            TimeProvider.System);
        FormsRoutes.Map(
            _app,
            _app.Services.GetRequiredService<IFormEngine>(),
            _app.Services.GetRequiredService<IFormCapabilityIssuer>(),
            _app.Services.GetRequiredService<IFormCapabilityVerifier>(),
            activeTeam,
            OperatorRoles,
            TimeProvider.System);

        await _app.StartAsync();
        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        _client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) };
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private async Task<string> CreateUnitAsync(string displayName)
    {
        var resp = await _client.PostAsJsonAsync($"{AssetBase}/entities", new { type = UnitType, displayName });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var created = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return created.GetProperty("id").GetString()!;
    }

    [Fact(DisplayName = "the seeded catalog form renders every item from the live engine")]
    public async Task Catalog_Form_Renders()
    {
        var view = await _client.GetFromJsonAsync<JsonElement>($"{FormsBase}/{LivingStandardCatalogForm.FormId}");
        var fieldNames = view.GetProperty("sections").EnumerateArray()
            .SelectMany(s => s.GetProperty("fields").EnumerateArray())
            .Select(f => f.GetProperty("name").GetString())
            .ToArray();

        // unit_ref + 6 rating items + 4 safety items = 11 fields.
        Assert.Equal(11, fieldNames.Length);
        Assert.Contains(ResidentialLivingStandardCatalog.PanelCondition, fieldNames);
        Assert.Contains(ResidentialLivingStandardCatalog.ExposedWiring, fieldNames);
    }

    [Fact(DisplayName = "LIVE: submitting the catalog form projects a condition assessment per rated item")]
    public async Task Submitting_The_Catalog_Projects_A_Condition_Per_Rated_Item()
    {
        var unitId = await CreateUnitAsync("Unit 12A");

        // Before: no condition history.
        var before = await _client.GetFromJsonAsync<JsonElement>($"{AssetBase}/entities/{unitId}/condition");
        Assert.Empty(before.GetProperty("history").EnumerateArray());

        // ONE act: submit the seeded catalog form through the REAL forms route with the unit ref + grades.
        var submit = await _client.PostAsJsonAsync(
            $"{FormsBase}/{LivingStandardCatalogForm.FormId}/submit",
            new
            {
                unit_ref = unitId,
                panel_condition = 4,
                outlet_function = 5,
                kitchen_sink = 3,
                range_condition = 4,
                toilet_condition = 2,
                bath_drainage = 5,
                exposed_wiring = true,
                gas_leak = true,
                kitchen_gfci = true,
                bath_gfci = true,
            });
        Assert.Equal(HttpStatusCode.Created, submit.StatusCode);

        // A clean submit — no projection/skips fields (every rated item landed).
        var body = await submit.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.TryGetProperty("skips", out _));

        // MANY artifacts: one typed condition assessment per rated item, with provenance to the item field.
        var after = await _client.GetFromJsonAsync<JsonElement>($"{AssetBase}/entities/{unitId}/condition");
        var history = after.GetProperty("history").EnumerateArray().ToArray();
        Assert.Equal(6, history.Length); // 6 rating items → 6 assessments (safety pass/fails are Wave-3b compute)

        var byField = history.ToDictionary(
            h => h.GetProperty("sourceField").GetString()!,
            h => h.GetProperty("grade").GetInt32());
        Assert.Equal(4, byField["/" + ResidentialLivingStandardCatalog.PanelCondition]);
        Assert.Equal(2, byField["/" + ResidentialLivingStandardCatalog.ToiletCondition]);
        Assert.All(history, h => Assert.Equal(LivingStandardCatalogForm.FormId, h.GetProperty("sourceForm").GetString()));
    }

    [Fact(DisplayName = "a partial submit projects only the filled rated items (unfilled items are a silent no-op)")]
    public async Task Partial_Submit_Projects_Only_Filled_Items()
    {
        var unitId = await CreateUnitAsync("Unit 3B");

        var submit = await _client.PostAsJsonAsync(
            $"{FormsBase}/{LivingStandardCatalogForm.FormId}/submit",
            new { unit_ref = unitId, panel_condition = 5 }); // only ONE rating item filled
        Assert.Equal(HttpStatusCode.Created, submit.StatusCode);

        var body = await submit.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.TryGetProperty("skips", out _)); // an unfilled item is a genuine no-op, never a skip

        var after = await _client.GetFromJsonAsync<JsonElement>($"{AssetBase}/entities/{unitId}/condition");
        var only = Assert.Single(after.GetProperty("history").EnumerateArray());
        Assert.Equal("/" + ResidentialLivingStandardCatalog.PanelCondition, only.GetProperty("sourceField").GetString());
        Assert.Equal(5, only.GetProperty("grade").GetInt32());
    }

    private sealed class SingleTeamAccessor : IActiveTeamAccessor
    {
        public SingleTeamAccessor(TeamContext active) => Active = active;
        public TeamContext? Active { get; }
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged;
        private void _keep() => ActiveChanged?.Invoke(this, new ActiveTeamChangedEventArgs(null, null));
    }
}
