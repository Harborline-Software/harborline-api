using Harborline.Api.Foundation.Authorization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Blocks.Assets.Registry.DependencyInjection;
using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.Packs.Dcp;
using Harborline.Api.Foundation.Packs.Export;
using Harborline.Api.Foundation.Packs.Serialization;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Foundation.Packs.Validation;
using Harborline.Api.Foundation.Packs.Verify;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.Kernel.Schema;
using Harborline.Api.LocalNodeHost.Data.Compose;
using Harborline.Api.LocalNodeHost.Data.Forms;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.PackProjection;
using Harborline.Api.LocalNodeHost.Health;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

/// <summary>
/// Pack Composer B-2a — the KEYSTONE route-level acceptance. Re-authors the hand-authored #127 General pack
/// (six asset types) IN the Composer, over the REAL ceremony routes: compose → affirm → export, then
/// verifies the produced pack round-trips to <c>Verified</c> and carries the SAME six types. Plus the three
/// negative gates over HTTP: export-before-affirmation (422), a non-general RegulatoryClass (422), a
/// snapshot-hash mismatch on affirm (409), and the A-1 permission fence (403). Uses
/// <see cref="PackComposeRoutes.Map"/> + <see cref="PackComposerRoutes.Map"/> — no test/prod wire drift.
/// </summary>
public sealed class PackComposeRouteTests : IAsyncLifetime
{
    private static readonly TeamId TeamA = new(Guid.Parse("aaaa0000-0000-0000-0000-0000000b2a00"));

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private KeyPair _key = null!;
    private IFormDefinitionStore _forms = null!;
    private ISchemaRegistry _schemas = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddLogging();
        builder.Services.AddInMemoryAssetTypeSystem();
        _app = builder.Build();

        // Seed the six General types as Pack seeds — as installing+projecting the hand-authored pack does.
        var registry = _app.Services.GetRequiredService<IEntityTypeRegistry>();
        GeneralPackFixture.SeedInto(registry);
        _forms = new InMemoryFormDefinitionStore(TimeProvider.System);
        _schemas = new InMemorySchemaRegistry(TimeProvider.System);
        await RegisterPublishedFormAsync("general.asset-intake", "2.3.4");

        _key = KeyPair.Generate();
        var signer = new Ed25519Signer(_key);
        var codec = new PackFileCodec();
        var canonicalizer = new PackContentCanonicalizer();
        var dcpCanonicalizer = new PackDcpCanonicalizer();
        var exporter = new PackExporter(
            canonicalizer, dcpCanonicalizer, new PackValidator(new PackContentPiiScanner()),
            new DcpValidator(DcpCounselRegister.FromEmbeddedResource()), codec, timeProvider: TimeProvider.System);
        var verifier = new PackVerifier(new Ed25519Verifier(), codec);
        var trustStore = new InMemoryPackTrustStore(new[]
        {
            new PackTrustRoot(TrustScope.OwnRoster, _key.PrincipalId, PackComposerRoutes.OwnRosterEpoch, TrustRootStatus.Current),
        });
        var activeTeam = new MutableActiveTeamAccessor(TeamContextFor(TeamA, "General Co"));
        var ceremony = new ComposeCeremony(
            registry,
            canonicalizer,
            dcpCanonicalizer,
            new InMemoryDraftCompositionStore(),
            forms: _forms,
            schemas: _schemas, clock: TimeProvider.System);
        var authz = TestPackGate.AllowAll();

        PackComposeRoutes.Map(_app, ceremony, exporter, signer, activeTeam, authz, TimeProvider.System, NullLogger.Instance);
        // The verify route (B-1a) so the produced pack can be round-trip-verified in-test.
        PackComposerRoutes.Map(_app, exporter, verifier, trustStore, signer, activeTeam, authz, TimeProvider.System, NullLogger.Instance);

        await _app.StartAsync();
        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        _client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) };
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        _key?.Dispose();
        (_forms as IDisposable)?.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private static object GeneralComposeBody(object? dcp = null) => new
    {
        key = GeneralPackFixture.PackKey,
        version = GeneralPackFixture.PackVersion,
        name = "General",
        description = "Harborline General starter pack.",
        scopeTier = "Horizontal",
        typeIds = GeneralPackFixture.TypeIds(),
        dcp,
    };

    [Fact(DisplayName = "KEYSTONE: re-author the General pack → compose/affirm/export → Verified, same six types")]
    public async Task General_pack_round_trips_through_the_ceremony()
    {
        // (1) Compose — snapshot the six selected types.
        var composeResp = await _client.PostAsJsonAsync(PackComposeRoutes.ComposeRoute, GeneralComposeBody());
        Assert.Equal(HttpStatusCode.OK, composeResp.StatusCode);
        using var composeDoc = JsonDocument.Parse(await composeResp.Content.ReadAsStringAsync());
        var root = composeDoc.RootElement;
        var composeId = root.GetProperty("composeId").GetString()!;
        var snapshotHash = root.GetProperty("snapshotHash").GetString()!;
        Assert.False(root.GetProperty("affirmed").GetBoolean());
        var leaves = root.GetProperty("leaves");
        Assert.Equal(6, leaves.GetArrayLength());
        // Every leaf carries the exact canonical bytes for the human review (S-1 — every leaf, not a subset).
        foreach (var leaf in leaves.EnumerateArray())
        {
            Assert.False(string.IsNullOrWhiteSpace(leaf.GetProperty("contentAddress").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(leaf.GetProperty("canonicalText").GetString()));
        }

        // (2) Affirm — bind the human PII review to the snapshot hash.
        var affirmResp = await _client.PostAsJsonAsync(
            $"{PackComposeRoutes.ComposeRoute}/{composeId}/affirm", new { snapshotHash });
        Assert.Equal(HttpStatusCode.OK, affirmResp.StatusCode);

        // (3) Export — re-hash + own-roster sign through the DCP gate.
        var exportResp = await _client.PostAsync($"{PackComposeRoutes.ComposeRoute}/{composeId}/export", null);
        Assert.Equal(HttpStatusCode.OK, exportResp.StatusCode);
        var packBytes = await exportResp.Content.ReadAsByteArrayAsync();
        Assert.NotEmpty(packBytes);

        // (4) The produced pack verifies own-roster.
        using var verifyContent = new ByteArrayContent(packBytes);
        verifyContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var verifyResp = await _client.PostAsync(PackComposerRoutes.VerifyRoute, verifyContent);
        Assert.Equal(HttpStatusCode.OK, verifyResp.StatusCode);
        using var verifyDoc = JsonDocument.Parse(await verifyResp.Content.ReadAsStringAsync());
        Assert.Equal("Verified", verifyDoc.RootElement.GetProperty("verdict").GetString());
        Assert.Equal(GeneralPackFixture.PackKey, verifyDoc.RootElement.GetProperty("manifestKey").GetString());

        // (5) The produced pack carries the SAME six types.
        var file = new PackFileCodec().TryDecode(packBytes);
        Assert.NotNull(file);
        var assetLeaves = file!.Contents
            .Where(c => c.Kind == Harborline.Api.Foundation.Packs.Model.PackContentKind.AssetTypeDefinition).ToList();
        Assert.Equal(6, assetLeaves.Count);
        // Per-item content-address match against the hand-authored types' canonical addresses.
        var expectedCids = GeneralPackFixture.ExpectedCanonicalCids();
        foreach (var payload in assetLeaves)
        {
            var actualCid = Harborline.Api.Foundation.Blobs.Cid.FromBytes(Convert.FromBase64String(payload.ContentBase64)).Value;
            Assert.True(expectedCids.TryGetValue(payload.Key, out var expectedCid), $"unexpected type {payload.Key}");
            Assert.Equal(expectedCid, actualCid);
        }

        // The draft was consumed.
        var reread = await _client.GetAsync($"{PackComposeRoutes.ComposeRoute}/{composeId}");
        Assert.Equal(HttpStatusCode.NotFound, reread.StatusCode);
    }

    [Fact(DisplayName = "export before affirmation is 422 (human PII review required)")]
    public async Task Export_before_affirm_is_422()
    {
        var composeId = await ComposeGeneralAsync();
        var exportResp = await _client.PostAsync($"{PackComposeRoutes.ComposeRoute}/{composeId}/export", null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, exportResp.StatusCode);
        Assert.Contains("affirmation_required", await exportResp.Content.ReadAsStringAsync());
    }

    [Fact(DisplayName = "a form-only compose snapshots the current published form and its schema semantics")]
    public async Task Form_only_pack_snapshots_published_form()
    {
        var body = new
        {
            key = "harborline.forms",
            version = "1.0.0",
            name = "Forms",
            description = "Published forms.",
            scopeTier = "Horizontal",
            typeIds = Array.Empty<string>(),
            formIds = new[] { "general.asset-intake" },
        };

        var response = await _client.PostAsJsonAsync(PackComposeRoutes.ComposeRoute, body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var leaf = Assert.Single(document.RootElement.GetProperty("leaves").EnumerateArray());
        Assert.Equal("general.asset-intake", leaf.GetProperty("key").GetString());
        Assert.Equal("FormDefinition", leaf.GetProperty("kind").GetString());
        Assert.Equal("2.3.4", leaf.GetProperty("version").GetString());

        var content = JsonNode.Parse(leaf.GetProperty("canonicalText").GetString()!);
        Assert.True(PackFormDefinitionContent.TryParse(content, out var request, out var error), error);
        var meta = request.FieldsMeta!["name"];
        Assert.True(meta.Required);
        Assert.Contains(meta.Validations!, validation => validation.Code == "minLength" && validation.Param == "3");

        var category = request.FieldsMeta["category"];
        Assert.Equal("select", category.Type);
        Assert.Equal(new[] { "standard", "priority" }, category.Options);

        var quantity = request.FieldsMeta["quantity"];
        Assert.Equal("number", quantity.Type);
        Assert.Contains(quantity.Validations!, validation => validation.Code == "minimum" && validation.Param == "1");
        Assert.Contains(quantity.Validations!, validation => validation.Code == "maximum" && validation.Param == "10");

        Assert.Equal("date", request.FieldsMeta["inspection-date"].Type);

        var amount = request.FieldsMeta["amount"];
        Assert.Equal("currency", amount.Type);
        Assert.DoesNotContain(amount.Validations ?? Array.Empty<FieldValidationDto>(),
            validation => validation.Code == "pattern");
    }

    [Fact(DisplayName = "a non-general RegulatoryClass hard-blocks at export (422, not cleared)")]
    public async Task Non_general_class_is_422()
    {
        var composeResp = await _client.PostAsJsonAsync(
            PackComposeRoutes.ComposeRoute, GeneralComposeBody(new { regulatoryClass = "financial" }));
        Assert.Equal(HttpStatusCode.OK, composeResp.StatusCode);
        using var doc = JsonDocument.Parse(await composeResp.Content.ReadAsStringAsync());
        var composeId = doc.RootElement.GetProperty("composeId").GetString()!;
        var snapshotHash = doc.RootElement.GetProperty("snapshotHash").GetString()!;

        await _client.PostAsJsonAsync($"{PackComposeRoutes.ComposeRoute}/{composeId}/affirm", new { snapshotHash });
        var exportResp = await _client.PostAsync($"{PackComposeRoutes.ComposeRoute}/{composeId}/export", null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, exportResp.StatusCode);
        Assert.Contains("not_cleared", await exportResp.Content.ReadAsStringAsync());
    }

    [Fact(DisplayName = "affirming a mismatched snapshot hash is 409 (S-1)")]
    public async Task Affirm_wrong_hash_is_409()
    {
        var composeId = await ComposeGeneralAsync();
        var affirmResp = await _client.PostAsJsonAsync(
            $"{PackComposeRoutes.ComposeRoute}/{composeId}/affirm", new { snapshotHash = "wrong-hash" });
        Assert.Equal(HttpStatusCode.Conflict, affirmResp.StatusCode);
        Assert.Contains("snapshot_mismatch", await affirmResp.Content.ReadAsStringAsync());
    }

    [Fact(DisplayName = "compose is denied without packages:author (A-1, 403)")]
    public async Task Compose_denied_without_permission()
    {
        // A fresh app whose gate grants NOTHING.
        using var app = await NewAppWithAuthzAsync(TestPackGate.Denying());
        var resp = await app.Client.PostAsJsonAsync(PackComposeRoutes.ComposeRoute, GeneralComposeBody());
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    private async Task<string> ComposeGeneralAsync()
    {
        var composeResp = await _client.PostAsJsonAsync(PackComposeRoutes.ComposeRoute, GeneralComposeBody());
        Assert.Equal(HttpStatusCode.OK, composeResp.StatusCode);
        using var doc = JsonDocument.Parse(await composeResp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("composeId").GetString()!;
    }

    private async Task RegisterPublishedFormAsync(string formId, string version)
    {
        var text = static (string value) => new InternationalizedTextDto(
            "en", new Dictionary<string, string> { ["en"] = value });
        var overlay = new OverlayDto(
            Fields: new Dictionary<string, FieldOverlayDto>
            {
                ["name"] = new(text("Name"), null, "text", "None"),
                ["category"] = new(text("Category"), null, null, "None"),
                ["quantity"] = new(text("Quantity"), null, "number", "None"),
                ["inspection-date"] = new(text("Inspection date"), null, "date", "None"),
                ["amount"] = new(text("Amount"), null, "currency", "None"),
            },
            Sections: new[]
            {
                new OverlaySectionDto("main", text("Main"),
                    new[] { "name", "category", "quantity", "inspection-date", "amount" }, null, null, null),
            },
            Rules: Array.Empty<RuleDto>(),
            Title: text("Asset intake"),
            Description: text("Collect asset details."));
        var saveRequest = new SaveFormDefinitionRequest(
            overlay,
            new Dictionary<string, FieldMetaDto>
            {
                ["name"] = new("text", true, null, new[] { new FieldValidationDto("minLength", "3") }),
                ["category"] = new("select", false, new[] { "standard", "priority" }),
                ["quantity"] = new("number", false, null, new[]
                {
                    new FieldValidationDto("minimum", "1"),
                    new FieldValidationDto("maximum", "10"),
                }),
                ["inspection-date"] = new("date", false, null),
                ["amount"] = new("currency", false, null),
            });
        var id = new FormDefinitionId(formId);
        var formVersion = SemanticVersion.Parse(version);
        var schema = await _schemas.RegisterAsync(BuilderSchemaSynthesizer.Synthesize(saveRequest, id));
        var definition = FormDefinitionRoutes.BuildDefinition(
            id, formVersion, NodeTenant.Resolve(new MutableActiveTeamAccessor(TeamContextFor(TeamA, "General Co"))),
            IdentityRef.System, schema.Id, overlay, DateTimeOffset.UtcNow);
        await _forms.RegisterAsync(definition);
        await _forms.PublishAsync(new DefinitionCoordinates(
            definition.Tenant, id.Value, formVersion.ToString()));
    }

    private static async Task<AppHandle> NewAppWithAuthzAsync(AuthorizationGate authz)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddLogging();
        builder.Services.AddInMemoryAssetTypeSystem();
        var app = builder.Build();

        var registry = app.Services.GetRequiredService<IEntityTypeRegistry>();
        GeneralPackFixture.SeedInto(registry);
        var key = KeyPair.Generate();
        var signer = new Ed25519Signer(key);
        var canonicalizer = new PackContentCanonicalizer();
        var dcpCanonicalizer = new PackDcpCanonicalizer();
        var exporter = new PackExporter(
            canonicalizer, dcpCanonicalizer, new PackValidator(new PackContentPiiScanner()),
            new DcpValidator(DcpCounselRegister.FromEmbeddedResource()), new PackFileCodec(), timeProvider: TimeProvider.System);
        var activeTeam = new MutableActiveTeamAccessor(TeamContextFor(TeamA, "General Co"));
        var ceremony = new ComposeCeremony(registry, canonicalizer, dcpCanonicalizer, new InMemoryDraftCompositionStore(), clock: TimeProvider.System);

        PackComposeRoutes.Map(app, ceremony, exporter, signer, activeTeam, authz, TimeProvider.System, NullLogger.Instance);
        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        return new AppHandle(app, new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) }, key);
    }

    private sealed record AppHandle(WebApplication App, HttpClient Client, KeyPair Key) : IDisposable
    {
        public void Dispose()
        {
            Client.Dispose();
            Key.Dispose();
            App.StopAsync().GetAwaiter().GetResult();
            ((IDisposable)App).Dispose();
        }
    }

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
