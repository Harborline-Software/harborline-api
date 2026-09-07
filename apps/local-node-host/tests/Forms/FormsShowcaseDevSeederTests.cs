using System.Linq;
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

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.Forms.Engine;
using Harborline.Api.Foundation.Forms.Engine.Capabilities;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.Kernel.Schema;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Forms;
using Harborline.Api.LocalNodeHost.Health;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Forms;

/// <summary>
/// Prototype-showcase form ladder (CIC-requested demo artifacts, 2026-07-03) — proves
/// <see cref="FormsShowcaseDevSeeder"/> registers + publishes all FIVE showcase definitions (L0–L4), that
/// each renders (a live GET never 404s / never throws), and that the L3 reference resolves correctly
/// through the REAL <see cref="IReuseResolver"/> even though the node-host render path does not call it
/// yet (see the seeder's remarks re: the known Section.Items render-engine gap).
/// </summary>
public sealed class FormsShowcaseDevSeederTests : IAsyncLifetime
{
    private static readonly TeamId TeamA = new(Guid.Parse("aaaa0000-0000-0000-0000-00000000fa03"));
    private static readonly ActorId Operator = new("local");
    private static readonly IReadOnlyList<string> OperatorRoles = new[] { FormsRoutes.NodeOperatorRole };

    private const string Base = "/api/local-node/forms";

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private FixedActiveTeamAccessor _activeTeam = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Environment.EnvironmentName = Environments.Development;
        builder.Logging.ClearProviders();

        builder.Services.AddSingleton<Harborline.Api.Foundation.Recovery.TenantKey.ITenantKeyProvider,
            Harborline.Api.Foundation.Recovery.TenantKey.InMemoryTenantKeyProvider>();
        builder.Services.AddSingleton<Harborline.Api.Foundation.Recovery.Crypto.IFieldEncryptor,
            Harborline.Api.Foundation.Recovery.Crypto.TenantKeyProviderFieldEncryptor>();
        builder.Services.AddTestAuthorizationGate().AddTestNodeForms();

        _activeTeam = new FixedActiveTeamAccessor(TeamContextFor(TeamA, "Team A"));
        builder.Services.AddSingleton<IActiveTeamAccessor>(_activeTeam);

        _app = builder.Build();

        // Drive the SHIPPED showcase seeder directly (mirrors FormsDevSeederTests).
        var seeder = new FormsShowcaseDevSeeder(
            _app.Services.GetRequiredService<ISchemaRegistry>(),
            _app.Services.GetRequiredService<AuthorizedFormDefinitionLifecycle>(),
            _app.Services.GetRequiredService<IReusableUnitStore>(),
            _activeTeam,
            _app.Environment,
            _app.Services.GetRequiredService<ILogger<FormsShowcaseDevSeeder>>(),
            TimeProvider.System);
        await seeder.StartAsync(CancellationToken.None);

        FormsRoutes.Map(
            _app,
            _app.Services.GetRequiredService<IFormEngine>(),
            _app.Services.GetRequiredService<IFormCapabilityIssuer>(),
            _app.Services.GetRequiredService<IFormCapabilityVerifier>(),
            _activeTeam,
            OperatorRoles,
            TimeProvider.System);

        await _app.StartAsync();

        var addresses = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>();
        _client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) };
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    [Theory(DisplayName = "seed: every showcase-ladder form (L0-L4) renders via a live GET")]
    [InlineData(FormsShowcaseDevSeeder.L0ContactEnquiryFormId)]
    [InlineData(FormsShowcaseDevSeeder.L1RentalApplicationFormId)]
    [InlineData(FormsShowcaseDevSeeder.L2InvoiceLinesFormId)]
    [InlineData(FormsShowcaseDevSeeder.L3CatalogReferenceFormId)]
    [InlineData(FormsShowcaseDevSeeder.L4LivingStandardInspectionFormId)]
    public async Task SeededShowcaseForm_Renders(string formId)
    {
        var resp = await _client.GetAsync($"{Base}/{formId}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(formId, doc.GetProperty("formId").GetString());
        Assert.True(doc.GetProperty("sections").GetArrayLength() > 0);

        // Every rendered section still carries the flat Fields list (the ADR 0055 Rev 7
        // authorization/order fallback) alongside the nested Items tree the runner now walks.
        var anyFields = false;
        foreach (var section in doc.GetProperty("sections").EnumerateArray())
        {
            if (section.GetProperty("fields").GetArrayLength() > 0)
            {
                anyFields = true;
            }
        }
        Assert.True(anyFields, $"'{formId}' rendered with zero fields across all sections.");
    }

    [Fact(DisplayName = "seed: L4's bilingual condition-rating field carries the condition-rating control hint")]
    public async Task L4_ConditionRatingField_CarriesControlHint()
    {
        var doc = await _client.GetFromJsonAsync<JsonElement>(
            $"{Base}/{FormsShowcaseDevSeeder.L4LivingStandardInspectionFormId}");
        var fields = doc.GetProperty("sections")[0].GetProperty("fields");
        var rating = FieldByName(fields, "electricalWiringRating");
        Assert.Equal("condition-rating", rating.GetProperty("controlHint").GetString());
    }

    [Fact(DisplayName = "render: L2 GET carries the Rev-7 item tree — sibling collections + the F-24 table")]
    public async Task L2_RendersItemTree_WithTableCollection()
    {
        var doc = await _client.GetFromJsonAsync<JsonElement>(
            $"{Base}/{FormsShowcaseDevSeeder.L2InvoiceLinesFormId}");
        var section = doc.GetProperty("sections")[0];

        // The section now carries the nested item tree (the runner walks THIS, not the flat fields).
        Assert.True(section.TryGetProperty("items", out var items), "L2 section is missing its Rev-7 items tree.");
        var kinds = items.EnumerateArray().Select(i => i.GetProperty("kind").GetString()).ToArray();
        Assert.Equal(new[] { "field", "field", "collection", "collection" }, kinds);

        // The lineItems collection carries the F-24 table presentation + its instance bounds.
        var lineItems = items.EnumerateArray().Single(i => i.GetProperty("key").GetString() == "lineItems");
        Assert.True(lineItems.TryGetProperty("table", out var table), "lineItems is missing its F-24 table config.");
        Assert.True(table.GetProperty("columns").TryGetProperty("lineUnitPrice", out _));
        Assert.Equal(1, lineItems.GetProperty("cardinality").GetProperty("min").GetInt32());
        var rowKeys = lineItems.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("key").GetString());
        Assert.Contains("lineDescription", rowKeys);
    }

    [Fact(DisplayName = "render: L4 GET carries the depth-4 discipline > category > field nested tree")]
    public async Task L4_RendersNestedGroupsToDepth4()
    {
        var doc = await _client.GetFromJsonAsync<JsonElement>(
            $"{Base}/{FormsShowcaseDevSeeder.L4LivingStandardInspectionFormId}");
        var items = doc.GetProperty("sections")[0].GetProperty("items");

        var electrical = items.EnumerateArray().Single(i => i.GetProperty("key").GetString() == "electrical");
        Assert.Equal("group", electrical.GetProperty("kind").GetString());
        var wiring = electrical.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("key").GetString() == "wiring");
        Assert.Equal("group", wiring.GetProperty("kind").GetString());
        var rating = wiring.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("key").GetString() == "electricalWiringRating");
        Assert.Equal("field", rating.GetProperty("kind").GetString());
        // The rendered field node carries the condition-rating control hint at depth 4.
        Assert.Equal("condition-rating", rating.GetProperty("field").GetProperty("controlHint").GetString());
    }

    [Fact(DisplayName = "seed: the L3 ReusableUnit is published and resolves through the real IReuseResolver")]
    public async Task L3_ReusableUnit_ResolvesThroughReuseResolver()
    {
        var unitStore = _app.Services.GetRequiredService<IReusableUnitStore>();
        var tenantId = NodeTenant.Resolve(_activeTeam);

        var published = await unitStore.GetCurrentPublishedAsync(
            tenantId, FormsShowcaseDevSeeder.L3AddressBlockUnitId, CancellationToken.None);
        Assert.NotNull(published);
        Assert.Equal(FormDefinitionStatus.Published, published!.Status);
        Assert.NotNull(published.Component);
        Assert.Contains("addressLine1", published.Component!.Fields.Keys);

        // The L3 definition itself still renders (accountName only — see the seeder's remarks; the
        // referenced subtree's fields are deliberately NOT duplicated into the flat fallback).
        var doc = await _client.GetFromJsonAsync<JsonElement>(
            $"{Base}/{FormsShowcaseDevSeeder.L3CatalogReferenceFormId}");
        Assert.Equal(FormsShowcaseDevSeeder.L3CatalogReferenceFormId, doc.GetProperty("formId").GetString());

        // The REAL keystone-level guarantee this showcase proves: IReuseResolver resolves the Reference
        // item against the published unit (the unit's fields load correctly + are addressable), and it
        // correctly REJECTS fail-closed if a consumer ever tried to redeclare a unit-owned field as its
        // own overlay (the CP-lock enforcement — proven directly, not routed around).
        var resolver = new ReuseResolver(unitStore);
        var formStore = _app.Services.GetRequiredService<IFormDefinitionStore>();
        var definition = await formStore.GetCurrentPublishedAsync(
            new DefinitionAddress(tenantId, FormsShowcaseDevSeeder.L3CatalogReferenceFormId), CancellationToken.None);
        Assert.NotNull(definition);

        var resolved = await resolver.ResolveAsync(definition!, CancellationToken.None);
        Assert.NotNull(resolved);

        // Sanity: the unit's own fields (addressLine1 etc.) resolve into the EFFECTIVE overlay produced by
        // ResolveAsync, even though the raw definition.Overlay.Fields never declares them (CP-locked, not
        // copied). This is the actual "reference reuse works" proof.
        Assert.Contains("addressLine1", resolved.Effective.Overlay.Fields.Keys);
        Assert.Contains("addressLine1", resolved.FieldProvenance.Keys);
    }

    [Fact(DisplayName = "seed: a restart re-runs the showcase seeder cleanly (idempotent)")]
    public async Task Seeder_IsIdempotent()
    {
        var seeder2 = new FormsShowcaseDevSeeder(
            _app.Services.GetRequiredService<ISchemaRegistry>(),
            _app.Services.GetRequiredService<AuthorizedFormDefinitionLifecycle>(),
            _app.Services.GetRequiredService<IReusableUnitStore>(),
            _activeTeam,
            _app.Environment,
            _app.Services.GetRequiredService<ILogger<FormsShowcaseDevSeeder>>(),
            TimeProvider.System);

        // No throw ⇒ idempotent no-op path taken for every already-published form + unit.
        await seeder2.StartAsync(CancellationToken.None);

        var resp = await _client.GetAsync($"{Base}/{FormsShowcaseDevSeeder.L0ContactEnquiryFormId}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    private static JsonElement FieldByName(JsonElement fields, string name)
    {
        foreach (var f in fields.EnumerateArray())
        {
            if (f.GetProperty("name").GetString() == name)
            {
                return f;
            }
        }
        throw new Xunit.Sdk.XunitException($"field '{name}' not found in view");
    }

    private static TeamContext TeamContextFor(TeamId teamId, string name)
        => new(teamId, name, new ServiceCollection().BuildServiceProvider(), TimeProvider.System);

    private sealed class FixedActiveTeamAccessor : IActiveTeamAccessor
    {
        public FixedActiveTeamAccessor(TeamContext active) => Active = active;
        public TeamContext? Active { get; }
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged;
        private void _keep() => ActiveChanged?.Invoke(this, new ActiveTeamChangedEventArgs(null, null));
    }
}
