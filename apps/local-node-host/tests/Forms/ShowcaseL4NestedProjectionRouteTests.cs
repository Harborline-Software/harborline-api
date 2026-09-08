using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.Blocks.Assets.Registry.Model;
using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.Forms.Engine;
using Harborline.Api.Foundation.Forms.Engine.Capabilities;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.Kernel.Schema;
using Harborline.Api.LocalNodeHost.Data.AssetRegistry;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Forms;
using Harborline.Api.LocalNodeHost.Health;

using Xunit;


namespace Harborline.Api.LocalNodeHost.Tests.Forms;

/// <summary>
/// ADR 0055 Rev 7 e2e — the L4 nested living-standard inspection submits through the REAL forms route as a
/// nested candidate (the value shape the tree renderer produces: groups nest under their key), passes the
/// nested JSON-Schema, and its NESTED condition-rating field projects into a typed
/// <see cref="ConditionAssessment"/>. This closes the loop the showcase surfaced: the tree the runner walks
/// submits, and its deep leaves still drive the "one act, two artifacts" projection.
/// </summary>
public sealed class ShowcaseL4NestedProjectionRouteTests : IAsyncLifetime
{
    private static readonly TeamId TeamA = new(Guid.Parse("aaaa0000-0000-0000-0000-00000000fb04"));
    private static readonly ActorId Operator = new("local");
    private static readonly IReadOnlyList<string> OperatorRoles = new[] { FormsRoutes.NodeOperatorRole };

    private const string AssetBase = "/api/local-node/asset-registry";
    private const string FormsBase = "/api/local-node/forms";

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private FixedActiveTeamAccessor _activeTeam = null!;
    private TenantId _tenant;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        builder.Services.AddSingleton<Harborline.Api.Foundation.Recovery.TenantKey.ITenantKeyProvider,
            Harborline.Api.Foundation.Recovery.TenantKey.InMemoryTenantKeyProvider>();
        builder.Services.AddSingleton<Harborline.Api.Foundation.Recovery.Crypto.IFieldEncryptor,
            Harborline.Api.Foundation.Recovery.Crypto.TenantKeyProviderFieldEncryptor>();

        builder.Services.AddTestAuthorizationGate().AddTestNodeForms();
        builder.Services.AddNodeAssetRegistry(TimeSpan.FromHours(1)); // no reconcile sweep mid-test

        _activeTeam = new FixedActiveTeamAccessor(TeamContextFor(TeamA));
        builder.Services.AddSingleton<IActiveTeamAccessor>(_activeTeam);

        _app = builder.Build();
        _tenant = NodeTenant.Resolve(_activeTeam);

        // Publish the showcase ladder (L4 among them) through the SHIPPED seeder.
        await new FormsShowcaseDevSeeder(
            _app.Services.GetRequiredService<ISchemaRegistry>(),
            _app.Services.GetRequiredService<AuthorizedFormDefinitionLifecycle>(),
            _app.Services.GetRequiredService<IReusableUnitStore>(),
            _activeTeam,
            _app.Environment,
            _app.Services.GetRequiredService<ILogger<FormsShowcaseDevSeeder>>(),
            TimeProvider.System).StartAsync(CancellationToken.None);

        // A target entity type the inspection rates.
        _app.Services.GetRequiredService<IEntityTypeRegistry>().SeedType(new EntityTypeSeed(
            new EntityTypeId("dwelling-unit"),
            new EntityTypeDescriptor("Dwelling Unit", EntityTrait.Maintainable),
            CascadeLayer.Pack));

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
            _activeTeam,
            TimeProvider.System);
        FormsRoutes.Map(
            _app,
            _app.Services.GetRequiredService<IFormEngine>(),
            _app.Services.GetRequiredService<IFormCapabilityIssuer>(),
            _app.Services.GetRequiredService<IFormCapabilityVerifier>(),
            _activeTeam,
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

    [Fact(DisplayName = "e2e: an L4 nested inspection submits through the tree and its deep rating projects")]
    public async Task L4NestedSubmit_ProjectsTheDeepConditionRating()
    {
        // The inspection targets a real registry entity.
        var unit = await CreateEntityAsync("dwelling-unit", "Unit 12B");

        // Bind L4's NESTED wiring-rating leaf (a full RFC-6901 pointer into the group tree) to that entity.
        await _app.Services.GetRequiredService<IConditionRatingFieldBindingStore>().RegisterAsync(
            _tenant,
            new ConditionRatingFieldBinding(
                new FormDefinitionId(FormsShowcaseDevSeeder.L4LivingStandardInspectionFormId),
                "/electrical/wiring/electricalWiringRating",
                ConditionEntityRefSource.Explicit,
                EntityRefKey: unit,
                ScaleMax: 5));

        // Submit the NESTED candidate the tree renderer produces (groups nest under their key). It satisfies
        // the L4 nested schema (electrical>wiring>rating required, plumbing>supply>rating required).
        var submit = await _client.PostAsJsonAsync(
            $"{FormsBase}/{FormsShowcaseDevSeeder.L4LivingStandardInspectionFormId}/submit",
            new
            {
                electrical = new { wiring = new { electricalWiringRating = 4 } },
                plumbing = new { supply = new { plumbingSupplyRating = 3 } },
            });

        Assert.Equal(HttpStatusCode.Created, submit.StatusCode);

        // The deep rating projected into a typed ConditionAssessment on the target entity.
        var history = await PollConditionHistoryAsync(unit);
        var record = Assert.Single(history.EnumerateArray());
        Assert.Equal(4, record.GetProperty("grade").GetInt32());
    }

    [Fact(DisplayName = "e2e: the nested L4 schema rejects a FLAT (pre-nesting) candidate")]
    public async Task L4FlatSubmit_IsRejectedByTheNestedSchema()
    {
        // A flat candidate (the pre-tree shape) no longer satisfies the nested L4 schema — proof the schema
        // now matches the tree the runner submits, not the old flat fallback.
        var submit = await _client.PostAsJsonAsync(
            $"{FormsBase}/{FormsShowcaseDevSeeder.L4LivingStandardInspectionFormId}/submit",
            new { electricalWiringRating = 4, plumbingSupplyRating = 3 });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, submit.StatusCode);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────

    private async Task<string> CreateEntityAsync(string type, string displayName)
    {
        var resp = await _client.PostAsJsonAsync($"{AssetBase}/entities", new { type, displayName });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    /// <summary>Polls the condition history until the projection lands (the runner runs it inline on submit,
    /// but a short bounded poll keeps the assertion robust under CI load — never sleep-and-hope).</summary>
    private async Task<JsonElement> PollConditionHistoryAsync(string entityId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var doc = await _client.GetFromJsonAsync<JsonElement>($"{AssetBase}/entities/{entityId}/condition");
            var history = doc.GetProperty("history");
            if (history.GetArrayLength() > 0 || DateTime.UtcNow > deadline)
            {
                return history;
            }
            await Task.Delay(50);
        }
    }

    private static TeamContext TeamContextFor(TeamId teamId)
        => new(teamId, "Team A", new ServiceCollection().BuildServiceProvider(), TimeProvider.System);

    private sealed class FixedActiveTeamAccessor : IActiveTeamAccessor
    {
        public FixedActiveTeamAccessor(TeamContext active) => Active = active;
        public TeamContext? Active { get; }
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged;
        private void _keep() => ActiveChanged?.Invoke(this, new ActiveTeamChangedEventArgs(null, null));
    }
}
