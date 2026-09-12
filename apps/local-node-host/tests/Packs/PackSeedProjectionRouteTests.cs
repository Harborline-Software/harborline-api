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
using Harborline.Api.Blocks.Assets.Registry.Model;
using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Blobs;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Documents.Issuance;
using Harborline.Api.Foundation.Documents.Model;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.Forms.Models;
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
using Harborline.Api.Kernel.Schema;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Forms;
using Harborline.Api.LocalNodeHost.Data.PackProjection;
using Harborline.Api.LocalNodeHost.Health;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

/// <summary>
/// BUILD #127 slice 1 — the seed-projection seam, end-to-end through the REAL routes. Proves the exact
/// CIC dogfood dead-end is closed: a first-party "General" pack of asset types, when installed AND
/// activated through the Pack Composer B-1b routes, PROJECTS into the shared entity-type registry so
/// <c>GET /asset-registry/types</c> — the Type Manager dropdown's source — now returns those types with
/// <c>Pack</c> provenance. Uses the SAME production route handlers (<see cref="PackComposerRoutes.Map"/> +
/// <see cref="PackInstallRoutes.Map"/> + <see cref="AssetRegistryRoutes.Map"/>) and the SAME
/// <see cref="PackSeedProjector"/> the host wires — no test/prod drift.
/// </summary>
public sealed class PackSeedProjectionRouteTests : IAsyncLifetime
{
    private static readonly TeamId TeamA = new(Guid.Parse("aaaa0000-0000-0000-0000-00000000127a"));

    private const string AssetTypesRoute = "/api/local-node/asset-registry/types";
    private const string PackKey = "harborline.general";
    private const string PropertyPackKey = "harborline.property-management";

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private KeyPair _key = null!;
    private IFormDefinitionStore _forms = null!;
    private IEntityTypeRegistry _registry = null!;
    private ISchemaRegistry _schemas = null!;
    private IWorkflowDefinitionStore _workflows = null!;
    private IWorkflowDefinitionExecutionStore _executionWorkflows = null!;
    private InMemoryDocumentTemplateRegistry _templates = null!;
    private InMemoryPackInstallStore _packStore = null!;
    private PackInstaller _installer = null!;
    private PackSeedProjector _projector = null!;
    private AuthorizedFormDefinitionLifecycle _authorizedForms = null!;
    private ICatalogue _catalogue = null!;
    private MutableActiveTeamAccessor _activeTeam = null!;
    private PackFileCodec _codec = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development",
        });
        builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("HARBORLINE_PACK_PROJECTION_TEST_URL") ?? "http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        // The SAME in-memory Asset-Type-System the node composes — the projector seeds it, the asset-registry
        // route reads it. One shared IEntityTypeRegistry singleton is the whole point of the assertion.
        builder.Services.AddLogging();
        builder.Services.AddInMemoryAssetTypeSystem();
        builder.Services.AddSingleton<Harborline.Api.Foundation.Recovery.TenantKey.ITenantKeyProvider,
            Harborline.Api.Foundation.Recovery.TenantKey.InMemoryTenantKeyProvider>();
        builder.Services.AddSingleton<Harborline.Api.Foundation.Recovery.Crypto.IFieldEncryptor,
            Harborline.Api.Foundation.Recovery.Crypto.TenantKeyProviderFieldEncryptor>();
        builder.Services.AddTestAuthorizationGate().AddTestNodeForms(
            configureWriters: static (services, entityMutations, _) =>
                services.AddEntityStoreWorkflowDefinitionStore(entityMutations));
        builder.Services.AddSingleton<ICapabilityAuthorityRegistry>(CapabilityAuthorityRegistry.Canonical);
        builder.Services.AddSingleton<IWorkflowAdmissionValidator, WorkflowAdmissionValidator>();
        _app = builder.Build();

        _key = KeyPair.Generate();
        var signer = new Ed25519Signer(_key);
        _codec = new PackFileCodec();
        var exporter = new PackExporter(
            new PackContentCanonicalizer(), new PackDcpCanonicalizer(),
            new PackValidator(new PackContentPiiScanner()),
            new DcpValidator(DcpCounselRegister.FromEmbeddedResource()), _codec, timeProvider: TimeProvider.System);
        var verifier = new PackVerifier(new Ed25519Verifier(), _codec);
        var trustStore = new InMemoryPackTrustStore(new[]
        {
            new PackTrustRoot(TrustScope.OwnRoster, _key.PrincipalId, PackComposerRoutes.OwnRosterEpoch, TrustRootStatus.Current),
        });
        _activeTeam = new MutableActiveTeamAccessor(TeamContextFor(TeamA, "General Co"));

        _packStore = new InMemoryPackInstallStore();
        var admission = new PackWorkflowAdmissionAdapter(new WorkflowAdmissionValidator());
        _installer = new PackInstaller(verifier, _packStore, admission, new InMemoryPackInstallAudit(),
            Harborline.Api.LocalNodeHost.Tests.Authorization.TestAuthorization.AllowGate());

        _registry = _app.Services.GetRequiredService<IEntityTypeRegistry>();
        _forms = _app.Services.GetRequiredService<IFormDefinitionStore>();
        _schemas = _app.Services.GetRequiredService<ISchemaRegistry>();
        _workflows = _app.Services.GetRequiredService<IWorkflowDefinitionStore>();
        _executionWorkflows = _app.Services.GetRequiredService<IWorkflowDefinitionExecutionStore>();
        _templates = new InMemoryDocumentTemplateRegistry();
        var roleGate = _app.Services.GetRequiredService<Harborline.Api.Foundation.Authorization.IRoleGateAdmission>();
        _authorizedForms = TestAuthorization.FormLifecycle(
            _forms, Harborline.Api.LocalNodeHost.Tests.Authorization.TestAuthorization.AllowGate(), roleGate);
        _catalogue = new ProjectedCatalogue(_authorizedForms);
        _projector = new PackSeedProjector(
            _packStore,
            _registry,
            NullLogger<PackSeedProjector>.Instance,
            templates: _templates,
            forms: _forms,
            schemas: _schemas,
            workflows: _workflows,
            time: TimeProvider.System,
            authorizedForms: _authorizedForms,
            authorizedWorkflows: TestAuthorization.WorkflowLifecycle(
                _workflows, Harborline.Api.LocalNodeHost.Tests.Authorization.TestAuthorization.AllowGate(),
                roleGate));

        var authz = TestPackGate.AllowAll();
        _app.Use(async (http, next) =>
        {
            http.Features.Set(DesktopPlaneRequestFeature.Instance);
            await next(http);
        });
        PackComposerRoutes.Map(_app, exporter, verifier, trustStore, signer, _activeTeam, authz, TimeProvider.System, NullLogger.Instance);
        PackInstallRoutes.Map(_app, _installer, _packStore, trustStore, PackRevocationList.Empty, _activeTeam,
            authz, TimeProvider.System, NullLogger.Instance, authorizingPrincipal: _key.PrincipalId.ToBase64Url(),
            projector: _projector);
        PackNavigationRoutes.Map(
            _app.MapDeviceReachableProductDataGroup(),
            _packStore,
            _activeTeam,
            NullLogger.Instance);
        AssetRegistryRoutes.Map(
            _app.MapDeviceReachableProductDataGroup(),
            _registry,
            _app.Services.GetRequiredService<IRegistryEntityRepository>(),
            _app.Services.GetRequiredService<ITypedRelationshipStore>(),
            _app.Services.GetRequiredService<IConditionAssessmentStore>(),
            _app.Services.GetRequiredService<IFormSubmissionRecordStore>(),
            _activeTeam,
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

    [Fact(DisplayName = "install → activate a General asset-type pack → GET /types returns the 6 (dead-end closed)")]
    public async Task Install_activate_projects_asset_types_into_the_dropdown()
    {
        // (0) THE DEAD-END, reproduced: a fresh node's type catalog is empty.
        Assert.Empty(await GetTypeIdsAsync());

        var packBytes = await ExportAsync(GeneralPackBody());

        // (1) Install (Draft) — the seed layer exists, but a Draft pack does NOT project yet.
        var install = await PostBytesAsync(PackInstallRoutes.InstallRoute, packBytes);
        Assert.Equal(HttpStatusCode.OK, install.StatusCode);
        Assert.Empty(await GetTypeIdsAsync()); // Draft ⇒ still empty (only Active packs project).

        // (2) Activate (Draft → Active) — the projection fires on the activate route.
        var activate = await _client.PostAsJsonAsync(PackInstallRoutes.ActivateRoute,
            new { packKey = PackKey, version = "1.0.0" });
        Assert.Equal(HttpStatusCode.OK, activate.StatusCode);

        // (3) THE ASSERTION: the dropdown source now returns the 6 pack asset types, provenance Pack.
        var types = await GetTypesAsync();
        var ids = types.Select(t => t.GetProperty("id").GetString()!).ToHashSet();
        Assert.Superset(
            new HashSet<string>
            {
                "general.equipment", "general.vehicle", "general.furniture",
                "general.it-computer", "general.tool", "general.facility",
            },
            ids);
        foreach (var t in types.Where(t => t.GetProperty("id").GetString()!.StartsWith("general.")))
        {
            Assert.Equal("Pack", t.GetProperty("provenance").GetString());
        }

        // (4) Idempotent: re-activating re-projects with no duplicate-seed fault + no growth.
        var reactivate = await _client.PostAsJsonAsync(PackInstallRoutes.ActivateRoute,
            new { packKey = PackKey, version = "1.0.0" });
        Assert.Equal(HttpStatusCode.OK, reactivate.StatusCode);
        Assert.Equal(6, (await GetTypeIdsAsync()).Count(id => id.StartsWith("general.")));
    }

    [Fact(DisplayName = "a malformed asset type in the pack is skipped, the valid ones still project")]
    public async Task Malformed_type_is_skipped_valid_ones_project()
    {
        var body = new
        {
            key = PackKey,
            version = "1.0.0",
            name = "General",
            description = "mixed validity",
            scopeTier = "Horizontal",
            contents = new object[]
            {
                AssetTypeContent("general.equipment", "Equipment", new[] { "Maintainable" }, 10, 5),
                // Trait-less ⇒ invalid per the pinned contract; must be skipped, not fatal.
                AssetTypeContent("general.broken", "Broken", Array.Empty<string>(), 1, 5),
            },
            dependencies = Array.Empty<object>(),
            capabilityRequirements = Array.Empty<string>(),
        };

        var packBytes = await ExportAsync(body);
        Assert.Equal(HttpStatusCode.OK, (await PostBytesAsync(PackInstallRoutes.InstallRoute, packBytes)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await _client.PostAsJsonAsync(PackInstallRoutes.ActivateRoute, new { packKey = PackKey, version = "1.0.0" })).StatusCode);

        var ids = await GetTypeIdsAsync();
        Assert.Contains("general.equipment", ids);
        Assert.DoesNotContain("general.broken", ids);
    }

    [Fact(DisplayName = "install → activate publishes a pack form at its pinned id/version; replay is idempotent")]
    public async Task Install_activate_projects_pinned_form_definition()
    {
        const string formId = "general.asset-intake";
        var packBytes = await ExportAsync(FormPackBody(formId, "2.3.4", invalidRule: false));

        Assert.Equal(HttpStatusCode.OK, (await PostBytesAsync(PackInstallRoutes.InstallRoute, packBytes)).StatusCode);
        var activate = await _client.PostAsJsonAsync(
            PackInstallRoutes.ActivateRoute,
            new { packKey = PackKey, version = "1.0.0" });
        Assert.Equal(HttpStatusCode.OK, activate.StatusCode);

        var tenant = NodeTenant.Resolve(_activeTeam);
        var published = await _forms.GetAsync(new DefinitionCoordinates(tenant, formId, "2.3.4"));
        Assert.Equal(FormDefinitionStatus.Published, published.Status);
        Assert.Equal(IdentityRef.System, published.Owner);

        var replay = await _client.PostAsJsonAsync(
            PackInstallRoutes.ActivateRoute,
            new { packKey = PackKey, version = "1.0.0" });
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);

        var revisions = new List<FormDefinition>();
        await foreach (var definition in _forms.ListByTenantAsync(tenant))
        {
            if (definition.Id.Value == formId)
            {
                revisions.Add(definition);
            }
        }
        Assert.Single(revisions);
    }

    [Fact(DisplayName = "ticket 176: a pack-projected catalogue definition retains its authored title and pack ownership")]
    public async Task Pack_Projected_Catalogue_Definition_Has_Authored_Title_And_Pack_Provenance()
    {
        const string formId = "general.catalogue-title";
        var packBytes = await ExportAsync(FormPackBody(formId, "1.0.0", invalidRule: false));

        Assert.Equal(HttpStatusCode.OK, (await PostBytesAsync(PackInstallRoutes.InstallRoute, packBytes)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsJsonAsync(
            PackInstallRoutes.ActivateRoute, new { packKey = PackKey, version = "1.0.0" })).StatusCode);

        var entries = await _catalogue.ListAsync(NodeTenant.Resolve(_activeTeam), PackContentKind.FormDefinition);
        var entry = Assert.Single(entries.Entries);
        Assert.Equal("Asset intake", entry.Title!.Values["en"]);
        Assert.Equal(PackKey, entry.Provenance.PackKey);
        Assert.Equal("1.0.0", entry.Provenance.PackVersion);
        Assert.Equal("pack", entry.Provenance.Kind);
    }

    [Fact(DisplayName = "ticket 176: a sealed compiled-kind claim is refused through pack admission and projects nothing")]
    public async Task Sealed_System_Type_Claim_Is_Refused_Through_Admission_Path()
    {
        const string sealedKey = "fOrMdEfInItIoN";
        var packBytes = await ExportAsync(FormPackBody(sealedKey, "1.0.0", invalidRule: false));

        Assert.Equal(HttpStatusCode.OK, (await PostBytesAsync(PackInstallRoutes.InstallRoute, packBytes)).StatusCode);
        var activation = await _client.PostAsJsonAsync(
            PackInstallRoutes.ActivateRoute, new { packKey = PackKey, version = "1.0.0" });
        Assert.Equal(HttpStatusCode.OK, activation.StatusCode);
        using var response = JsonDocument.Parse(await activation.Content.ReadAsStringAsync());
        var refusal = Assert.Single(response.RootElement.GetProperty("projectionRefusals").EnumerateArray());
        Assert.Equal(sealedKey, refusal.GetProperty("contentKey").GetString());
        Assert.Equal(PackSealedSystemTypeAdmission.RefusedCode, refusal.GetProperty("code").GetString());

        var tenant = NodeTenant.Resolve(_activeTeam);
        Assert.Null(await _forms.GetCurrentPublishedAsync(new DefinitionAddress(tenant, sealedKey)));
        var entries = await _catalogue.ListAsync(tenant, PackContentKind.FormDefinition);
        Assert.Empty(entries.Entries);
    }

    [Fact(DisplayName = "ticket 176: a key that merely contains a compiled kind name is admitted")]
    public async Task Substring_Of_Sealed_System_Type_Name_Is_Admitted_Through_Pack_Admission()
    {
        const string formId = "FormDefinitionTemplate";
        var packBytes = await ExportAsync(FormPackBody(formId, "1.0.0", invalidRule: false));

        Assert.Equal(HttpStatusCode.OK, (await PostBytesAsync(PackInstallRoutes.InstallRoute, packBytes)).StatusCode);
        var activation = await _client.PostAsJsonAsync(
            PackInstallRoutes.ActivateRoute, new { packKey = PackKey, version = "1.0.0" });
        Assert.Equal(HttpStatusCode.OK, activation.StatusCode);
        using var response = JsonDocument.Parse(await activation.Content.ReadAsStringAsync());
        Assert.Empty(response.RootElement.GetProperty("projectionRefusals").EnumerateArray());
        Assert.NotNull(await _forms.GetCurrentPublishedAsync(
            new DefinitionAddress(NodeTenant.Resolve(_activeTeam), formId)));
    }

    [Fact(DisplayName = "pack form schema registration writes nothing to the blob store, incl. on a replayed activation")]
    public async Task Pack_form_schema_registration_writes_no_blobs()
    {
        // Card 3776: the schema registry used to side-store canonical bytes in the
        // node IBlobStore and discard the returned CID — one orphaned blob per
        // active-pack schema per boot. Assert store STATE: nothing lands there on
        // first projection, and a replayed activation (the repeat-boot pass) still
        // writes nothing.
        const string formId = "general.blobless-intake";
        var packBytes = await ExportAsync(FormPackBody(formId, "2.3.4", invalidRule: false));

        Assert.Equal(HttpStatusCode.OK, (await PostBytesAsync(PackInstallRoutes.InstallRoute, packBytes)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await _client.PostAsJsonAsync(PackInstallRoutes.ActivateRoute, new { packKey = PackKey, version = "1.0.0" })).StatusCode);

        var tenant = NodeTenant.Resolve(_activeTeam);
        var published = await _forms.GetAsync(new DefinitionCoordinates(tenant, formId, "2.3.4"));
        var schema = await _schemas.GetAsync(published.SchemaRef);
        Assert.NotNull(schema);

        var blobs = _app.Services.GetRequiredService<Harborline.Api.Foundation.Blobs.IBlobStore>();
        Assert.False(await blobs.ExistsLocallyAsync(schema!.ContentAddress));

        // Repeat boot pass: re-activate and assert the store is still untouched.
        var replay = await _client.PostAsJsonAsync(
            PackInstallRoutes.ActivateRoute, new { packKey = PackKey, version = "1.0.0" });
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.False(await blobs.ExistsLocallyAsync(schema.ContentAddress));
    }

    [Fact(DisplayName = "an uncompilable pack form is skipped without rolling back pack activation")]
    public async Task Invalid_form_admission_does_not_brick_activation()
    {
        const string formId = "general.broken-form";
        var packBytes = await ExportAsync(FormPackBody(formId, "1.0.0", invalidRule: true));

        Assert.Equal(HttpStatusCode.OK, (await PostBytesAsync(PackInstallRoutes.InstallRoute, packBytes)).StatusCode);
        var activate = await _client.PostAsJsonAsync(
            PackInstallRoutes.ActivateRoute,
            new { packKey = PackKey, version = "1.0.0" });
        Assert.Equal(HttpStatusCode.OK, activate.StatusCode);
        using (var response = JsonDocument.Parse(await activate.Content.ReadAsStringAsync()))
        {
            var refusal = Assert.Single(
                response.RootElement.GetProperty("projectionRefusals").EnumerateArray());
            Assert.Equal(formId, refusal.GetProperty("contentKey").GetString());
            Assert.Equal("FormDefinition", refusal.GetProperty("contentKind").GetString());
            Assert.Equal(FormDefinitionCodes.RulesUncompilable, refusal.GetProperty("code").GetString());
        }

        var tenant = NodeTenant.Resolve(_activeTeam);
        Assert.Null(await _forms.GetCurrentPublishedAsync(new DefinitionAddress(tenant, formId)));
        Assert.Equal(
            PackLifecycleState.Active,
            Assert.Single(_packStore.ListInstalled(tenant)).Lifecycle);
    }

    [Fact(DisplayName = "pack projection does NOT inherit the authoring placeholder gate — signed content labelled \"New field\" still publishes")]
    public async Task Placeholder_labelled_pack_form_still_projects_and_publishes()
    {
        // Ticket 157 review (P1): the placeholder refusal is an AUTHORING-time rule. Applying
        // it at pack projection would retroactively invalidate previously-valid signed pack
        // content — on upgrade/re-projection the form silently never (re)publishes and the
        // Draft-resume recovery path can loop Invalid forever, stranding the pinned tuple.
        const string formId = "general.placeholder-label";
        var body = JsonSerializer.SerializeToNode(FormPackBody(formId, "1.0.0", invalidRule: false))!.AsObject();
        body["contents"]![0]!["content"]!["overlay"]!["fields"]!["name"]!["label"] =
            JsonSerializer.SerializeToNode(Text("New field"));

        var packBytes = await ExportAsync(body);
        Assert.Equal(HttpStatusCode.OK, (await PostBytesAsync(PackInstallRoutes.InstallRoute, packBytes)).StatusCode);
        var activate = await _client.PostAsJsonAsync(
            PackInstallRoutes.ActivateRoute, new { packKey = PackKey, version = "1.0.0" });
        Assert.Equal(HttpStatusCode.OK, activate.StatusCode);
        using (var response = JsonDocument.Parse(await activate.Content.ReadAsStringAsync()))
        {
            Assert.Empty(response.RootElement.GetProperty("projectionRefusals").EnumerateArray());
        }

        var tenant = NodeTenant.Resolve(_activeTeam);
        var published = await _forms.GetAsync(new DefinitionCoordinates(tenant, formId, "1.0.0"));
        Assert.Equal(FormDefinitionStatus.Published, published.Status);
    }

    [Fact(DisplayName = "a pack form carrying 'draft: true' is refused at content parse — the flag can never pretend to stage")]
    public async Task Draft_flagged_pack_form_is_refused_as_malformed()
    {
        const string formId = "general.draft-flagged";
        var body = JsonSerializer.SerializeToNode(FormPackBody(formId, "1.0.0", invalidRule: false))!.AsObject();
        body["contents"]![0]!["content"]!["draft"] = true;

        var packBytes = await ExportAsync(body);
        Assert.Equal(HttpStatusCode.OK, (await PostBytesAsync(PackInstallRoutes.InstallRoute, packBytes)).StatusCode);
        var activate = await _client.PostAsJsonAsync(
            PackInstallRoutes.ActivateRoute, new { packKey = PackKey, version = "1.0.0" });
        Assert.Equal(HttpStatusCode.OK, activate.StatusCode);
        using (var response = JsonDocument.Parse(await activate.Content.ReadAsStringAsync()))
        {
            var refusal = Assert.Single(
                response.RootElement.GetProperty("projectionRefusals").EnumerateArray());
            Assert.Equal(formId, refusal.GetProperty("contentKey").GetString());
            Assert.Equal(PackSeedProjector.FormMalformedCode, refusal.GetProperty("code").GetString());
        }

        var tenant = NodeTenant.Resolve(_activeTeam);
        Assert.Null(await _forms.GetCurrentPublishedAsync(new DefinitionAddress(tenant, formId)));
    }

    [Fact(DisplayName = "a vendor pack envelope claiming Base is refused before form persistence")]
    public async Task Vendor_pack_form_cannot_claim_the_platform_layer()
    {
        const string formId = "general.forged-platform-form";
        const string formVersion = "1.0.0";
        var tenant = NodeTenant.Resolve(_activeTeam);
        var body = JsonSerializer.SerializeToNode(
            FormPackBody(formId, formVersion, invalidRule: false))!.AsObject();
        body["contents"]![0]!["content"]!["definitionEnvelope"] = JsonSerializer.SerializeToNode(
            new DefinitionEnvelope<FormDefinitionId, SemanticVersion, TenantId, FormDefinitionProvenance>(
                new FormDefinitionId(formId),
                SemanticVersion.Parse(formVersion),
                tenant,
                CascadeLayer.Base,
                new FormDefinitionProvenance(IdentityRef.System, null),
                Array.Empty<DefinitionRequirement>()),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
            });

        var packBytes = await ExportAsync(body);
        Assert.Equal(HttpStatusCode.OK, (await PostBytesAsync(PackInstallRoutes.InstallRoute, packBytes)).StatusCode);
        var activate = await _client.PostAsJsonAsync(
            PackInstallRoutes.ActivateRoute, new { packKey = PackKey, version = "1.0.0" });

        Assert.Equal(HttpStatusCode.OK, activate.StatusCode);
        using var response = JsonDocument.Parse(await activate.Content.ReadAsStringAsync());
        var refusal = Assert.Single(response.RootElement.GetProperty("projectionRefusals").EnumerateArray());
        Assert.Equal(formId, refusal.GetProperty("contentKey").GetString());
        Assert.Equal(PackSeedProjector.FormPinnedTupleConflictCode, refusal.GetProperty("code").GetString());
        Assert.Equal(PackKey, Assert.Single(_packStore.ListInstalled(tenant)).PackKey);
        Assert.Null(await _forms.GetCurrentPublishedAsync(new DefinitionAddress(tenant, formId)));
    }

    [Fact(DisplayName = "startup projection resumes a matching draft left between form register and publish")]
    public async Task Startup_projection_recovers_matching_registered_draft()
    {
        const string formId = "general.restart-recovery";
        const string formVersion = "3.0.1";
        var packBytes = await ExportAsync(FormPackBody(formId, formVersion, invalidRule: false));
        Assert.Equal(HttpStatusCode.OK, (await PostBytesAsync(PackInstallRoutes.InstallRoute, packBytes)).StatusCode);

        var tenant = NodeTenant.Resolve(_activeTeam);
        var draft = await RegisterInstalledFormDraftAsync(tenant, IdentityRef.System);

        var activated = _installer.Activate(tenant, PackKey, "1.0.0", DateTimeOffset.UtcNow, "test-operator");
        Assert.True(activated.Activated);
        Assert.True(activated.Projected);

        var recovered = await _forms.GetAsync(new DefinitionCoordinates(
            tenant, draft.Id.Value, draft.Version.ToString()));
        Assert.Equal(FormDefinitionStatus.Published, recovered.Status);
        Assert.Equal(IdentityRef.System, recovered.Owner);
    }

    [Fact(DisplayName = "startup projection failure is logged without bricking node boot")]
    public async Task Startup_projection_failure_does_not_escape_the_hosted_service()
    {
        var startup = new PackSeedProjectionHostedService(
            new ThrowingProjectionReconciler(),
            _activeTeam,
            NullLogger<PackSeedProjectionHostedService>.Instance);

        await startup.StartAsync(CancellationToken.None);
    }

    [Fact(DisplayName = "restart projects only an incomplete admitted transition")]
    public async Task Restart_reconciles_incomplete_admitted_activation()
    {
        const string formId = "general.admitted-restart";
        var packBytes = await ExportAsync(FormPackBody(formId, "1.0.0", invalidRule: false));
        Assert.Equal(HttpStatusCode.OK, (await PostBytesAsync(PackInstallRoutes.InstallRoute, packBytes)).StatusCode);
        var tenant = NodeTenant.Resolve(_activeTeam);
        var interrupted = NewInstaller(new ThrowingProjectionDispatcher());

        var activated = interrupted.Activate(
            tenant, PackKey, "1.0.0", DateTimeOffset.UtcNow, "test-operator");
        Assert.True(activated.Activated);
        Assert.False(activated.Projected);
        Assert.Single(((IPackProjectionAdmissionStore)_packStore).ListIncompleteProjectionAdmissions());
        Assert.Null(await _forms.GetCurrentPublishedAsync(new DefinitionAddress(tenant, formId)));

        var restarted = NewInstaller(_projector);
        var hosted = new PackSeedProjectionHostedService(
            (IPackInstaller)restarted, _activeTeam, NullLogger<PackSeedProjectionHostedService>.Instance);
        await hosted.StartAsync(CancellationToken.None);

        Assert.NotNull(await _forms.GetCurrentPublishedAsync(new DefinitionAddress(tenant, formId)));
        Assert.Empty(((IPackProjectionAdmissionStore)_packStore).ListIncompleteProjectionAdmissions());
    }

    [Fact(DisplayName = "startup reconciles incomplete admissions for a tenant other than the active team")]
    public async Task Restart_reconciles_second_tenant_incomplete_admission()
    {
        const string workflowKey = "general.second-tenant-recovery";
        var secondTenant = new TenantId("tenant-not-active-team");
        CommitWorkflowPackDirectly(secondTenant, workflowKey, "1.0.0", admissible: true);
        var interrupted = NewInstaller(new ThrowingProjectionDispatcher());

        var activated = interrupted.Activate(
            secondTenant, PackKey, "1.0.0", DateTimeOffset.UtcNow, "test-operator");
        Assert.True(activated.Activated);
        Assert.Contains(
            ((IPackProjectionAdmissionStore)_packStore).ListIncompleteProjectionAdmissions(),
            admission => admission.Tenant == secondTenant);

        var restarted = NewInstaller(_projector);
        var hosted = new PackSeedProjectionHostedService(
            (IPackInstaller)restarted, _activeTeam, NullLogger<PackSeedProjectionHostedService>.Instance);
        await hosted.StartAsync(CancellationToken.None);

        var projected = await _workflows.GetAsync(
            new DefinitionCoordinates(secondTenant, workflowKey, "1.0.0"));
        Assert.Equal(WorkflowDefinitionStatus.Published, projected.Status);
        Assert.DoesNotContain(
            ((IPackProjectionAdmissionStore)_packStore).ListIncompleteProjectionAdmissions(),
            admission => admission.Tenant == secondTenant);
    }

    [Fact(DisplayName = "startup never projects an active row without admission evidence")]
    public async Task Restart_ignores_active_pack_without_admission_evidence()
    {
        const string workflowKey = "general.no-admission";
        var tenant = NodeTenant.Resolve(_activeTeam);
        CommitWorkflowPackDirectly(tenant, workflowKey, "1.0.0", admissible: true);
        _packStore.Activate(tenant, PackKey, "1.0.0");
        var restarted = NewInstaller(_projector);
        var hosted = new PackSeedProjectionHostedService(
            (IPackInstaller)restarted, _activeTeam, NullLogger<PackSeedProjectionHostedService>.Instance);

        await hosted.StartAsync(CancellationToken.None);

        await Assert.ThrowsAsync<WorkflowDefinitionNotFoundException>(async () =>
            await _workflows.GetAsync(new DefinitionCoordinates(tenant, workflowKey, "1.0.0")));
    }

    [Fact(DisplayName = "a pinned tuple owned by a non-system author is refused, never overwritten or published")]
    public async Task Existing_non_pack_owned_tuple_is_refused()
    {
        const string formId = "general.ownership-conflict";
        const string formVersion = "1.4.0";
        var packBytes = await ExportAsync(FormPackBody(formId, formVersion, invalidRule: false));
        Assert.Equal(HttpStatusCode.OK, (await PostBytesAsync(PackInstallRoutes.InstallRoute, packBytes)).StatusCode);

        var tenant = NodeTenant.Resolve(_activeTeam);
        var authoredOwner = new IdentityRef("user", "local-author");
        await RegisterInstalledFormDraftAsync(tenant, authoredOwner);

        var activate = await _client.PostAsJsonAsync(
            PackInstallRoutes.ActivateRoute,
            new { packKey = PackKey, version = "1.0.0" });
        Assert.Equal(HttpStatusCode.OK, activate.StatusCode);
        using (var response = JsonDocument.Parse(await activate.Content.ReadAsStringAsync()))
        {
            var refusal = Assert.Single(
                response.RootElement.GetProperty("projectionRefusals").EnumerateArray());
            Assert.Equal(formId, refusal.GetProperty("contentKey").GetString());
            Assert.Equal(
                PackSeedProjector.FormPinnedTupleConflictCode,
                refusal.GetProperty("code").GetString());
        }

        var existing = await _forms.GetAsync(new DefinitionCoordinates(tenant, formId, formVersion));
        Assert.Equal(FormDefinitionStatus.Draft, existing.Status);
        Assert.Equal(authoredOwner, existing.Owner);
        Assert.Null(await _forms.GetCurrentPublishedAsync(new DefinitionAddress(tenant, formId)));
    }

    [Fact(DisplayName = "an already-published form owned by another pack refuses instead of resuming")]
    public async Task Existing_published_form_owned_by_another_pack_is_refused()
    {
        const string formId = "general.other-pack-published-form";
        const string formVersion = "1.4.0";
        var packBytes = await ExportAsync(FormPackBody(formId, formVersion, invalidRule: false));
        Assert.Equal(HttpStatusCode.OK, (await PostBytesAsync(PackInstallRoutes.InstallRoute, packBytes)).StatusCode);
        var tenant = NodeTenant.Resolve(_activeTeam);
        var existing = await RegisterInstalledFormDraftAsync(tenant, IdentityRef.System, "other-pack");
        await _forms.PublishAsync(new DefinitionCoordinates(tenant, formId, formVersion));

        var activate = await _client.PostAsJsonAsync(
            PackInstallRoutes.ActivateRoute, new { packKey = PackKey, version = "1.0.0" });
        using var response = JsonDocument.Parse(await activate.Content.ReadAsStringAsync());
        var refusal = Assert.Single(response.RootElement.GetProperty("projectionRefusals").EnumerateArray());
        Assert.Equal(PackProjectionAuthorityCodes.SourceMismatch, refusal.GetProperty("code").GetString());

        var persisted = await _forms.GetAsync(
            new DefinitionCoordinates(tenant, existing.Id.Value, existing.Version.ToString()));
        Assert.Equal("other-pack", persisted.PackSource!.PackId);
    }

    [Fact(DisplayName = "install → activate publishes a pinned System-owned Pack workflow; replay is a no-op")]
    public async Task Install_activate_projects_pinned_workflow_definition()
    {
        const string workflowKey = "general.invoice-approval";
        const string workflowVersion = "2.3.4";
        var packBytes = await ExportAsync(WorkflowPackBody(workflowKey, workflowVersion, admissible: true));

        Assert.Equal(HttpStatusCode.OK, (await PostBytesAsync(PackInstallRoutes.InstallRoute, packBytes)).StatusCode);
        var activate = await _client.PostAsJsonAsync(
            PackInstallRoutes.ActivateRoute,
            new { packKey = PackKey, version = "1.0.0" });
        Assert.Equal(HttpStatusCode.OK, activate.StatusCode);
        using (var response = JsonDocument.Parse(await activate.Content.ReadAsStringAsync()))
        {
            Assert.Empty(response.RootElement.GetProperty("projectionRefusals").EnumerateArray());
        }

        var tenant = NodeTenant.Resolve(_activeTeam);
        var published = await _workflows.GetAsync(new DefinitionCoordinates(tenant, workflowKey, workflowVersion));
        Assert.Equal(WorkflowDefinitionStatus.Published, published.Status);
        Assert.Equal(workflowKey, published.Authored.GetProperty("key").GetString());
        Assert.Equal(workflowVersion, published.Authored.GetProperty("version").GetString());
        Assert.Equal(tenant.Value, published.Authored.GetProperty("tenant").GetString());
        Assert.Equal("system", published.Authored.GetProperty("owner").GetProperty("scheme").GetString());
        Assert.Equal("__sunfish", published.Authored.GetProperty("owner").GetProperty("value").GetString());
        Assert.Equal("Pack", published.Authored.GetProperty("provenance").GetString());

        var replay = await _projector.ProjectActivePacksAsync(tenant);
        Assert.Equal(1, replay.WorkflowDefinitionsAlreadyPresent);
        Assert.Equal(0, replay.WorkflowDefinitionsPublished);
        Assert.Empty(replay.Refusals);

        var revisions = new List<WorkflowDefinitionRecord>();
        await foreach (var definition in _workflows.ListByTenantAsync(tenant))
        {
            if (definition.Key == workflowKey)
            {
                revisions.Add(definition);
            }
        }
        Assert.Single(revisions);
    }

    [Fact(DisplayName = "workflow projection resumes a matching draft left between register and publish")]
    public async Task Workflow_projection_recovers_matching_registered_draft()
    {
        const string workflowKey = "general.workflow-restart-recovery";
        const string workflowVersion = "3.0.1";
        var packBytes = await ExportAsync(WorkflowPackBody(workflowKey, workflowVersion, admissible: true));
        Assert.Equal(HttpStatusCode.OK, (await PostBytesAsync(PackInstallRoutes.InstallRoute, packBytes)).StatusCode);

        var tenant = NodeTenant.Resolve(_activeTeam);
        var authoredNode = JsonNode.Parse(
            JsonSerializer.Serialize(WorkflowContent(workflowKey, workflowVersion, admissible: true)))!.AsObject();
        authoredNode["tenant"] = tenant.Value;
        authoredNode["status"] = WorkflowDefinitionStatus.Published.ToString();
        authoredNode["owner"] = new JsonObject
        {
            ["scheme"] = IdentityRef.System.Scheme,
            ["value"] = IdentityRef.System.Value,
        };
        authoredNode["provenance"] = CascadeLayer.Pack.ToString();
        using var authoredDocument = JsonDocument.Parse(authoredNode.ToJsonString());
        var authored = authoredDocument.RootElement.Clone();
        var model = WorkflowDefinitionWireMapper.ToModel(
            authored, tenant.Value, workflowKey, workflowVersion, CascadeLayer.Pack);
        model = new WorkflowDefinition
        {
            Envelope = model.Envelope,
            Status = model.Status,
            SubjectFormRef = model.SubjectFormRef,
            Mutability = model.Mutability,
            InitialState = model.InitialState,
            States = model.States,
            Transitions = model.Transitions,
            Triggers = model.Triggers,
            Actions = model.Actions,
            GuardRuleIds = model.GuardRuleIds,
            PackSource = new PackProjectionSource(PackKey, "1.0.0"),
        };
        await _workflows.RegisterAsync(model, authored);

        var activated = _installer.Activate(tenant, PackKey, "1.0.0", DateTimeOffset.UtcNow, "test-operator");
        Assert.True(activated.Activated);
        Assert.True(activated.Projected);
        var recovered = await _workflows.GetAsync(new DefinitionCoordinates(tenant, workflowKey, workflowVersion));
        Assert.Equal(WorkflowDefinitionStatus.Published, recovered.Status);
    }

    [Fact(DisplayName = "a Draft pack's workflow is not projected before activation")]
    public async Task Inactive_workflow_definition_is_not_projected()
    {
        const string workflowKey = "general.inactive-workflow";
        var packBytes = await ExportAsync(WorkflowPackBody(workflowKey, "1.0.0", admissible: true));
        Assert.Equal(HttpStatusCode.OK, (await PostBytesAsync(PackInstallRoutes.InstallRoute, packBytes)).StatusCode);

        var tenant = NodeTenant.Resolve(_activeTeam);
        var projection = await _projector.ProjectActivePacksAsync(tenant);

        Assert.Equal(0, projection.WorkflowDefinitionsPublished);
        Assert.Equal(0, projection.WorkflowDefinitionsAlreadyPresent);
        Assert.Empty(projection.Refusals);
        await Assert.ThrowsAsync<WorkflowDefinitionNotFoundException>(
            async () => await _workflows.GetAsync(new DefinitionCoordinates(tenant, workflowKey, "1.0.0")));
        Assert.Equal(PackLifecycleState.Draft, Assert.Single(_packStore.ListInstalled(tenant)).Lifecycle);
    }

    [Fact(DisplayName = "an inadmissible Active workflow is refused with its localizable code without deactivation")]
    public async Task Invalid_workflow_admission_does_not_mutate_activation()
    {
        const string workflowKey = "general.unclassified-workflow";
        const string workflowVersion = "1.0.0";
        var tenant = NodeTenant.Resolve(_activeTeam);
        CommitWorkflowPackDirectly(tenant, workflowKey, workflowVersion, admissible: false);
        var activate = await _client.PostAsJsonAsync(
            PackInstallRoutes.ActivateRoute,
            new { packKey = PackKey, version = "1.0.0" });

        Assert.Equal(HttpStatusCode.OK, activate.StatusCode);
        using (var response = JsonDocument.Parse(await activate.Content.ReadAsStringAsync()))
        {
            var refusal = Assert.Single(
                response.RootElement.GetProperty("projectionRefusals").EnumerateArray());
            Assert.Equal(workflowKey, refusal.GetProperty("contentKey").GetString());
            Assert.Equal("WorkflowDefinition", refusal.GetProperty("contentKind").GetString());
            Assert.Equal(WorkflowAdmissionCodes.ActionUnclassified, refusal.GetProperty("code").GetString());
        }
        Assert.Equal(PackLifecycleState.Active, Assert.Single(_packStore.ListInstalled(tenant)).Lifecycle);
        await Assert.ThrowsAsync<WorkflowDefinitionNotFoundException>(
            async () => await _workflows.GetAsync(new DefinitionCoordinates(tenant, workflowKey, workflowVersion)));
    }

    [Fact(DisplayName = "a workflow pinned-tuple collision is refused with a stable code and never overwritten")]
    public async Task Existing_authored_workflow_tuple_is_refused()
    {
        const string workflowKey = "general.workflow-ownership-conflict";
        const string workflowVersion = "1.4.0";
        var tenant = NodeTenant.Resolve(_activeTeam);
        var authored = JsonSerializer.SerializeToElement(WorkflowContent(
            workflowKey, workflowVersion, admissible: true));
        var model = WorkflowDefinitionWireMapper.ToModel(
            authored, tenant.Value, workflowKey, workflowVersion);
        await _workflows.RegisterAsync(model, authored);

        var packBytes = await ExportAsync(WorkflowPackBody(workflowKey, workflowVersion, admissible: true));
        Assert.Equal(HttpStatusCode.OK, (await PostBytesAsync(PackInstallRoutes.InstallRoute, packBytes)).StatusCode);
        var activate = await _client.PostAsJsonAsync(
            PackInstallRoutes.ActivateRoute,
            new { packKey = PackKey, version = "1.0.0" });
        Assert.Equal(HttpStatusCode.OK, activate.StatusCode);
        using (var response = JsonDocument.Parse(await activate.Content.ReadAsStringAsync()))
        {
            var refusal = Assert.Single(
                response.RootElement.GetProperty("projectionRefusals").EnumerateArray());
            Assert.Equal(
                PackSeedProjector.WorkflowPinnedTupleConflictCode,
                refusal.GetProperty("code").GetString());
        }

        var existing = await _workflows.GetAsync(new DefinitionCoordinates(tenant, workflowKey, workflowVersion));
        Assert.Equal(WorkflowDefinitionStatus.Draft, existing.Status);
        Assert.Equal("pack-author", existing.Authored.GetProperty("owner").GetProperty("scheme").GetString());
        Assert.Equal("ignored", existing.Authored.GetProperty("provenance").GetString());
        Assert.Equal(PackLifecycleState.Active, Assert.Single(_packStore.ListInstalled(tenant)).Lifecycle);
    }

    [Fact(DisplayName = "an already-published workflow owned by another pack refuses instead of resuming")]
    public async Task Existing_published_workflow_owned_by_another_pack_is_refused()
    {
        const string workflowKey = "general.other-pack-published-workflow";
        const string workflowVersion = "1.4.0";
        var tenant = NodeTenant.Resolve(_activeTeam);
        var packBytes = await ExportAsync(WorkflowPackBody(workflowKey, workflowVersion, admissible: true));
        Assert.Equal(HttpStatusCode.OK, (await PostBytesAsync(PackInstallRoutes.InstallRoute, packBytes)).StatusCode);

        var authoredNode = JsonSerializer.SerializeToNode(
            WorkflowContent(workflowKey, workflowVersion, admissible: true))!.AsObject();
        authoredNode["tenant"] = tenant.Value;
        authoredNode["status"] = WorkflowDefinitionStatus.Published.ToString();
        authoredNode["owner"] = new JsonObject
        {
            ["scheme"] = IdentityRef.System.Scheme,
            ["value"] = IdentityRef.System.Value,
        };
        authoredNode["provenance"] = CascadeLayer.Pack.ToString();
        using var authoredDocument = JsonDocument.Parse(authoredNode.ToJsonString());
        var authored = authoredDocument.RootElement.Clone();
        var parsed = WorkflowDefinitionWireMapper.ToModel(
            authored, tenant.Value, workflowKey, workflowVersion, CascadeLayer.Pack);
        var model = new WorkflowDefinition
        {
            Envelope = parsed.Envelope,
            Status = parsed.Status,
            SubjectFormRef = parsed.SubjectFormRef,
            Mutability = parsed.Mutability,
            InitialState = parsed.InitialState,
            States = parsed.States,
            Transitions = parsed.Transitions,
            Triggers = parsed.Triggers,
            Actions = parsed.Actions,
            GuardRuleIds = parsed.GuardRuleIds,
            PackSource = new PackProjectionSource("other-pack", "1.0.0"),
        };
        await _workflows.RegisterAsync(
            model, authored,
            new WorkflowDefinitionRegistrationOptions(DateTimeOffset.UtcNow, WorkflowDefinitionStatus.Published));

        var activate = await _client.PostAsJsonAsync(
            PackInstallRoutes.ActivateRoute, new { packKey = PackKey, version = "1.0.0" });
        using var response = JsonDocument.Parse(await activate.Content.ReadAsStringAsync());
        var refusal = Assert.Single(response.RootElement.GetProperty("projectionRefusals").EnumerateArray());
        Assert.Equal(PackProjectionAuthorityCodes.SourceMismatch, refusal.GetProperty("code").GetString());
        var persisted = await _workflows.GetAsync(
            new DefinitionCoordinates(tenant, workflowKey, workflowVersion));
        Assert.Equal("other-pack", persisted.PackSource!.PackId);
    }

    [Fact(DisplayName = "deactivate retracts live asset/form/workflow content; reactivate restores retained state")]
    public async Task Deactivate_retracts_runtime_content_and_reactivate_restores_it()
    {
        const string assetTypeId = "general.equipment";
        const string formId = "general.asset-intake";
        const string formVersion = "2.3.4";
        const string workflowKey = "general.invoice-approval";
        const string workflowVersion = "2.3.4";
        var tenant = NodeTenant.Resolve(_activeTeam);

        var packBytes = await ExportAsync(ReversiblePackBody(
            assetTypeId, formId, formVersion, workflowKey, workflowVersion));
        Assert.Equal(HttpStatusCode.OK, (await PostBytesAsync(PackInstallRoutes.InstallRoute, packBytes)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await _client.PostAsJsonAsync(
                PackInstallRoutes.ActivateRoute,
                new { packKey = PackKey, version = "1.0.0" })).StatusCode);

        var seed = Assert.IsType<EntityTypeSeed>(_registry.GetSeed(assetTypeId));
        await _registry.OverrideSeedAsync(
            tenant,
            assetTypeId,
            seed.Descriptor with { DisplayName = "Tenant equipment" },
            new Harborline.Api.Foundation.Assets.Common.Instant(DateTimeOffset.UtcNow));
        Assert.Equal(FormDefinitionStatus.Published,
            (await _forms.GetAsync(new DefinitionCoordinates(tenant, formId, formVersion))).Status);
        Assert.Equal(WorkflowDefinitionStatus.Published,
            (await _workflows.GetAsync(new DefinitionCoordinates(tenant, workflowKey, workflowVersion))).Status);

        var deactivate = await _client.PostAsJsonAsync(
            PackInstallRoutes.DeactivateRoute,
            new { packKey = PackKey, version = "1.0.0" });
        Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);
        using (var response = JsonDocument.Parse(await deactivate.Content.ReadAsStringAsync()))
        {
            Assert.Empty(response.RootElement.GetProperty("projectionRefusals").EnumerateArray());
        }

        Assert.DoesNotContain(assetTypeId, await GetTypeIdsAsync());
        Assert.Null(_registry.GetSeed(assetTypeId));
        Assert.Null(await _registry.GetTypeAsync(tenant, assetTypeId));
        Assert.Null(await _forms.GetCurrentPublishedAsync(new DefinitionAddress(tenant, formId)));
        Assert.Equal(FormDefinitionStatus.Withdrawn,
            (await _forms.GetAsync(new DefinitionCoordinates(tenant, formId, formVersion))).Status);
        Assert.Null(await _workflows.GetCurrentPublishedAsync(new DefinitionAddress(tenant, workflowKey)));
        Assert.Equal(WorkflowDefinitionStatus.Withdrawn,
            (await _workflows.GetAsync(new DefinitionCoordinates(tenant, workflowKey, workflowVersion))).Status);
        await Assert.ThrowsAsync<WorkflowDefinitionNotFoundException>(
            () => _executionWorkflows.GetAdmittedAsync(new DefinitionCoordinates(
                tenant, workflowKey, workflowVersion)).AsTask());

        var reactivate = await _client.PostAsJsonAsync(
            PackInstallRoutes.ActivateRoute,
            new { packKey = PackKey, version = "1.0.0" });
        Assert.Equal(HttpStatusCode.OK, reactivate.StatusCode);

        Assert.Contains(assetTypeId, await GetTypeIdsAsync());
        Assert.Equal("Tenant equipment", (await _registry.GetTypeAsync(tenant, assetTypeId))!.Descriptor.DisplayName);
        Assert.Equal(FormDefinitionStatus.Published,
            (await _forms.GetCurrentPublishedAsync(new DefinitionAddress(tenant, formId)))!.Status);
        Assert.Equal(WorkflowDefinitionStatus.Published,
            (await _workflows.GetCurrentPublishedAsync(new DefinitionAddress(tenant, workflowKey)))!.Status);
        Assert.Equal(WorkflowDefinitionStatus.Published,
            (await _executionWorkflows.GetAdmittedAsync(new DefinitionCoordinates(
                tenant, workflowKey, workflowVersion))).Status);
    }

    [Fact(DisplayName = "property pack installs over General and deterministically projects nav + rent template")]
    public async Task Property_pack_projects_ratified_navigation_and_template()
    {
        await InstallAndActivateGeneralAsync();

        var request = LoadPropertyPackRequest();
        var packBytes = await ExportAsync(request);
        var decoded = _codec.TryDecode(packBytes);
        Assert.NotNull(decoded);
        Assert.NotNull(decoded.Dcp);
        Assert.Equal("General", decoded.Envelope!.Payload.Manifest.Dcp!.RegulatoryClass.ToString());

        var install = await PostBytesAsync(PackInstallRoutes.InstallRoute, packBytes);
        Assert.Equal(HttpStatusCode.OK, install.StatusCode);

        var tenant = NodeTenant.Resolve(_activeTeam);
        var draft = _packStore.GetVersion(tenant, PropertyPackKey, "1.0.0");
        Assert.NotNull(draft);
        Assert.Equal(PackLifecycleState.Draft, draft.Lifecycle);
        Assert.Collection(
            draft.Dependencies,
            dependency =>
            {
                Assert.Equal(PackKey, dependency.Key);
                Assert.Equal("1.0.0", dependency.Version);
                Assert.Empty(dependency.DeclaredDependencyKeys);
            });
        Assert.Equal(
            new[] { "property-management.navigation", "property-management.rent-invoice" },
            draft.SeedItems.Select(item => item.Key).ToArray());
        foreach (var item in draft.SeedItems)
        {
            var body = Assert.IsType<JsonObject>(item.ParseContent());
            Assert.False(body.ContainsKey("owner"));
            Assert.False(body.ContainsKey("tenant"));
            Assert.False(body.ContainsKey("packKey"));
            Assert.False(body.ContainsKey("provenance"));
        }

        var immutableSeeds = draft.SeedItems.ToDictionary(
            item => item.Key,
            item => (item.ContentAddress, item.CanonicalJson));

        var activate = await _client.PostAsJsonAsync(
            PackInstallRoutes.ActivateRoute,
            new { packKey = PropertyPackKey, version = "1.0.0" });
        Assert.Equal(HttpStatusCode.OK, activate.StatusCode);
        using (var activationJson = JsonDocument.Parse(await activate.Content.ReadAsStringAsync()))
        {
            Assert.Empty(activationJson.RootElement.GetProperty("projectionRefusals").EnumerateArray());
        }

        var firstNavigation = await _client.GetAsync(PackNavigationRoutes.NavigationRoute);
        Assert.Equal(HttpStatusCode.OK, firstNavigation.StatusCode);
        var firstNavigationBytes = await firstNavigation.Content.ReadAsByteArrayAsync();
        using (var navigationJson = JsonDocument.Parse(firstNavigationBytes))
        {
            var workspace = Assert.Single(
                navigationJson.RootElement.GetProperty("pack").GetProperty("seedWorkspaces")
                    .EnumerateArray().ToArray());
            Assert.Equal("property-management", workspace.GetProperty("id").GetString());
            Assert.Equal("workspaces.property-management", workspace.GetProperty("labelKey").GetString());
            Assert.Equal(
                new[] { "manager", "maintenance" },
                workspace.GetProperty("defaultForPersonas").EnumerateArray()
                    .Select(persona => persona.GetString()).ToArray());
            Assert.Equal(
                new[] { "portfolio", "billing", "documents" },
                workspace.GetProperty("groups").EnumerateArray()
                    .Select(group => group.GetProperty("id").GetString()).ToArray());
        }

        var template = _templates.Resolve("property-management.rent-invoice", "1.0.0");
        Assert.NotNull(template);
        Assert.Equal(tenant, template.Envelope.Tenant);
        Assert.Equal("invoice", template.DocumentType);
        Assert.Equal(new RecordTypeBinding("invoice", "1"), template.RecordType);
        Assert.Equal(DocumentLocalePolicy.Fixed("en-US"), template.Locale);
        Assert.Equal(4, template.Structure.Count);

        var replay = await _client.PostAsJsonAsync(
            PackInstallRoutes.ActivateRoute,
            new { packKey = PropertyPackKey, version = "1.0.0" });
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using (var replayJson = JsonDocument.Parse(await replay.Content.ReadAsStringAsync()))
        {
            Assert.Empty(replayJson.RootElement.GetProperty("projectionRefusals").EnumerateArray());
        }

        var secondNavigation = await _client.GetAsync(PackNavigationRoutes.NavigationRoute);
        Assert.Equal(HttpStatusCode.OK, secondNavigation.StatusCode);
        Assert.Equal(firstNavigationBytes, await secondNavigation.Content.ReadAsByteArrayAsync());

        var active = _packStore.GetActive(tenant, PropertyPackKey);
        Assert.NotNull(active);
        Assert.Equal(PackLifecycleState.Active, active.Lifecycle);
        Assert.Equal(
            immutableSeeds,
            active.SeedItems.ToDictionary(item => item.Key, item => (item.ContentAddress, item.CanonicalJson)));
    }

    [Fact(DisplayName = "malformed property nav + template refuse with stable codes without bricking activation")]
    public async Task Malformed_property_content_is_admission_gated()
    {
        await InstallAndActivateGeneralAsync();

        var request = LoadPropertyPackRequest();
        request["key"] = "harborline.property-management-malformed";
        var contents = Assert.IsType<JsonArray>(request["contents"]);
        var navigation = Assert.IsType<JsonObject>(Assert.IsType<JsonObject>(contents[0])["content"]);
        navigation.Remove("seedWorkspaces");
        navigation["title"] = "legacy placeholder";
        var template = Assert.IsType<JsonObject>(Assert.IsType<JsonObject>(contents[1])["content"]);
        template["structure"] = new JsonArray();

        var packBytes = await ExportAsync(request);
        Assert.Equal(HttpStatusCode.OK,
            (await PostBytesAsync(PackInstallRoutes.InstallRoute, packBytes)).StatusCode);

        var activate = await _client.PostAsJsonAsync(
            PackInstallRoutes.ActivateRoute,
            new { packKey = "harborline.property-management-malformed", version = "1.0.0" });
        Assert.Equal(HttpStatusCode.OK, activate.StatusCode);
        using (var activationJson = JsonDocument.Parse(await activate.Content.ReadAsStringAsync()))
        {
            var refusal = Assert.Single(
                activationJson.RootElement.GetProperty("projectionRefusals").EnumerateArray());
            Assert.Equal("property-management.rent-invoice", refusal.GetProperty("contentKey").GetString());
            Assert.Equal("TemplateDefinition", refusal.GetProperty("contentKind").GetString());
            Assert.Equal(PackSeedProjector.TemplateMalformedCode, refusal.GetProperty("code").GetString());
        }

        Assert.Null(_templates.Resolve("property-management.rent-invoice", "1.0.0"));
        var tenant = NodeTenant.Resolve(_activeTeam);
        Assert.Equal(
            PackLifecycleState.Active,
            _packStore.GetActive(tenant, "harborline.property-management-malformed")!.Lifecycle);

        var navigationResponse = await _client.GetAsync(PackNavigationRoutes.NavigationRoute);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, navigationResponse.StatusCode);
        using var navigationJson = JsonDocument.Parse(await navigationResponse.Content.ReadAsStringAsync());
        Assert.Equal("pack.nav.malformed", navigationJson.RootElement.GetProperty("code").GetString());
        Assert.Equal(
            PackLifecycleState.Active,
            _packStore.GetActive(tenant, "harborline.property-management-malformed")!.Lifecycle);
    }

    [Theory]
    [InlineData("key", "property-management.wrong", PackSeedProjector.TemplateKeyMismatchCode)]
    [InlineData("version", "9.9.9", PackSeedProjector.TemplateVersionMismatchCode)]
    public async Task Template_body_cannot_override_verified_envelope(
        string field,
        string value,
        string expectedCode)
    {
        await InstallAndActivateGeneralAsync();

        var request = LoadPropertyPackRequest();
        var contents = Assert.IsType<JsonArray>(request["contents"]);
        var template = Assert.IsType<JsonObject>(Assert.IsType<JsonObject>(contents[1])["content"]);
        template[field] = value;

        var packBytes = await ExportAsync(request);
        Assert.Equal(HttpStatusCode.OK,
            (await PostBytesAsync(PackInstallRoutes.InstallRoute, packBytes)).StatusCode);
        var activate = await _client.PostAsJsonAsync(
            PackInstallRoutes.ActivateRoute,
            new { packKey = PropertyPackKey, version = "1.0.0" });
        Assert.Equal(HttpStatusCode.OK, activate.StatusCode);

        using var activationJson = JsonDocument.Parse(await activate.Content.ReadAsStringAsync());
        var refusal = Assert.Single(
            activationJson.RootElement.GetProperty("projectionRefusals").EnumerateArray());
        Assert.Equal(expectedCode, refusal.GetProperty("code").GetString());
        Assert.Equal(
            PackLifecycleState.Active,
            _packStore.GetActive(NodeTenant.Resolve(_activeTeam), PropertyPackKey)!.Lifecycle);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    private async Task InstallAndActivateGeneralAsync()
    {
        var generalBytes = await ExportAsync(GeneralPackBody());
        Assert.Equal(HttpStatusCode.OK,
            (await PostBytesAsync(PackInstallRoutes.InstallRoute, generalBytes)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await _client.PostAsJsonAsync(
                PackInstallRoutes.ActivateRoute,
                new { packKey = PackKey, version = "1.0.0" })).StatusCode);
    }

    private static JsonObject LoadPropertyPackRequest()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Packs", "property-management-pack.export.json");
        return Assert.IsType<JsonObject>(JsonNode.Parse(File.ReadAllText(path)));
    }

    private async Task<List<JsonElement>> GetTypesAsync()
    {
        var resp = await _client.GetAsync(AssetTypesRoute);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("types").EnumerateArray().Select(e => e.Clone()).ToList();
    }

    private async Task<List<string>> GetTypeIdsAsync()
        => (await GetTypesAsync()).Select(t => t.GetProperty("id").GetString()!).ToList();

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

    private PackInstaller NewInstaller(IPackProjectionDispatcher dispatcher) => new(
        new PackVerifier(new Ed25519Verifier(), _codec),
        _packStore,
        new PackWorkflowAdmissionAdapter(new WorkflowAdmissionValidator()),
        new InMemoryPackInstallAudit(),
        Harborline.Api.LocalNodeHost.Tests.Authorization.TestAuthorization.AllowGate(),
        dispatcher);

    private async Task<FormDefinition> RegisterInstalledFormDraftAsync(
        TenantId tenant,
        IdentityRef owner,
        string packSourceId = PackKey)
    {
        var installed = Assert.Single(_packStore.ListInstalled(tenant));
        var item = Assert.Single(installed.SeedItems);
        Assert.True(
            PackFormDefinitionContent.TryParse(item.ParseContent(), out var request, out var parseError),
            parseError);

        var id = new FormDefinitionId(item.Key);
        var version = SemanticVersion.Parse(item.Version);
        var schemaJson = BuilderSchemaSynthesizer.Synthesize(request, id);
        var schema = await _schemas.RegisterAsync(schemaJson);
        var draft = FormDefinitionRoutes.BuildDefinition(
            id, version, tenant, owner, schema.Id, request.Overlay, DateTimeOffset.UtcNow);
        if (owner == IdentityRef.System)
        {
            draft = draft with
            {
                Envelope = draft.Envelope with { CascadeLayer = CascadeLayer.Pack },
                Overlay = draft.Overlay with
                {
                    Sections = draft.Overlay.Sections
                        .Select(section => section with { Access = new SectionAccess([], []) })
                        .ToArray(),
                },
                PackSource = new PackProjectionSource(packSourceId, installed.Version),
            };
        }
        FormDefinitionPublishAdmission.ValidateOrThrow(draft);
        await _forms.RegisterAsync(draft);
        return draft;
    }

    /// <summary>The 6-type General starter pack — the canonical content mirrored in
    /// <c>_shared/packs/general/general-pack.export.json</c> (the dogfood seed source).</summary>
    private static object GeneralPackBody() => new
    {
        key = PackKey,
        version = "1.0.0",
        name = "General",
        description = "Harborline General starter pack — baseline business asset types (slice 1).",
        scopeTier = "Horizontal",
        contents = new object[]
        {
            AssetTypeContent("general.equipment", "Equipment", new[] { "Maintainable" }, 10, 5),
            AssetTypeContent("general.vehicle", "Vehicle", new[] { "Movable", "Maintainable" }, 8, 5),
            AssetTypeContent("general.furniture", "Furniture", new[] { "Movable" }, 15, 5),
            AssetTypeContent("general.it-computer", "IT / Computer", new[] { "Movable", "Maintainable" }, 4, 5),
            AssetTypeContent("general.tool", "Tool", new[] { "Movable", "Maintainable" }, 7, 5),
            AssetTypeContent("general.facility", "Property / Facility", new[] { "Container" }, 40, 5),
        },
        dependencies = Array.Empty<object>(),
        capabilityRequirements = Array.Empty<string>(),
    };

    private static object AssetTypeContent(string id, string displayName, string[] traits, int life, int scale) => new
    {
        key = id,
        kind = "AssetTypeDefinition",
        version = "1.0.0",
        content = new
        {
            id,
            displayName,
            traits,
            expectedUsefulLifeYears = life,
            conditionScaleMax = scale,
        },
    };

    private static object FormPackBody(string formId, string formVersion, bool invalidRule) => new
    {
        key = PackKey,
        version = "1.0.0",
        name = "General forms",
        description = "Pack-projected form proof.",
        scopeTier = "Horizontal",
        contents = new object[]
        {
            new
            {
                key = formId,
                kind = "FormDefinition",
                version = formVersion,
                content = new
                {
                    overlay = new
                    {
                        fields = new Dictionary<string, object>
                        {
                            ["name"] = new
                            {
                                label = Text("Name"),
                                controlHint = "text",
                                piiSensitivity = "None",
                            },
                        },
                        sections = new object[]
                        {
                            new { id = "main", title = Text("Main"), fields = new[] { "name" } },
                        },
                        rules = invalidRule
                            ? new object[]
                            {
                                new
                                {
                                    id = "broken-rule",
                                    tier = "JsonLogic",
                                    scope = "Field",
                                    scopeTarget = "name",
                                    expression = "{not valid json",
                                    action = "Validate",
                                },
                            }
                            : Array.Empty<object>(),
                        title = Text("Asset intake"),
                        description = Text("Collect asset details."),
                    },
                    fieldsMeta = new Dictionary<string, object>
                    {
                        ["name"] = new { type = "text", required = true, options = (string[]?)null },
                    },
                },
            },
        },
        dependencies = Array.Empty<object>(),
        capabilityRequirements = Array.Empty<string>(),
    };

    private static object WorkflowPackBody(string workflowKey, string workflowVersion, bool admissible) => new
    {
        key = PackKey,
        version = "1.0.0",
        name = "General workflows",
        description = "Pack-projected workflow proof.",
        scopeTier = "Horizontal",
        contents = new object[]
        {
            new
            {
                key = workflowKey,
                kind = "WorkflowDefinition",
                version = workflowVersion,
                content = WorkflowContent(workflowKey, workflowVersion, admissible),
            },
        },
        dependencies = Array.Empty<object>(),
        capabilityRequirements = Array.Empty<string>(),
    };

    private static JsonObject ReversiblePackBody(
        string assetTypeId,
        string formId,
        string formVersion,
        string workflowKey,
        string workflowVersion)
    {
        var body = JsonSerializer.SerializeToNode(
            FormPackBody(formId, formVersion, invalidRule: false))!.AsObject();
        var contents = body["contents"]!.AsArray();
        contents.Insert(0, JsonSerializer.SerializeToNode(
            AssetTypeContent(assetTypeId, "Equipment", new[] { "Maintainable" }, 10, 5)));
        var workflowBody = JsonSerializer.SerializeToNode(
            WorkflowPackBody(workflowKey, workflowVersion, admissible: true))!.AsObject();
        contents.Add(workflowBody["contents"]!.AsArray()[0]!.DeepClone());
        return body;
    }

    private static object WorkflowContent(string workflowKey, string workflowVersion, bool admissible) => new
    {
        key = workflowKey,
        version = workflowVersion,
        status = "Published",
        tenant = "pack-authored-tenant-is-ignored",
        owner = new { scheme = "pack-author", value = "ignored" },
        provenance = "ignored",
        title = Text("Invoice approval"),
        mutability = "Locked",
        initialState = "Draft",
        states = new object[]
        {
            new { id = "Draft", label = Text("Draft"), kind = "Normal" },
            new { id = "PendingApproval", label = Text("Pending approval"), kind = "Normal" },
            new { id = "Posted", label = Text("Posted"), kind = "Terminal" },
        },
        triggers = new object[]
        {
            new { id = "issued", kind = "Event", eventType = "Issued" },
            new { id = "approve", kind = "HumanAction", task = "invoice-approval" },
        },
        transitions = new object[]
        {
            new { id = "t-issue", from = "Draft", on = "issued", to = "PendingApproval" },
            new { id = "t-approve", from = "PendingApproval", on = "approve", to = "Posted" },
        },
        actions = new object[]
        {
            new
            {
                id = "a-post-je",
                on = new { transition = "t-approve" },
                kind = "CreateRecord",
                capabilityRef = "ledger.post-journal-entry",
                classification = admissible ? "CP" : null,
            },
        },
        guards = Array.Empty<object>(),
        createdAt = "2026-07-15T00:00:00Z",
        updatedAt = "2026-07-15T00:00:00Z",
    };

    private void CommitWorkflowPackDirectly(
        TenantId tenant,
        string workflowKey,
        string workflowVersion,
        bool admissible)
    {
        var canonicalJson = JsonSerializer.Serialize(WorkflowContent(workflowKey, workflowVersion, admissible));
        var seed = new PackSeedItem(
            workflowKey,
            PackContentKind.WorkflowDefinition,
            workflowVersion,
            canonicalJson,
            Cid.FromBytes(System.Text.Encoding.UTF8.GetBytes(canonicalJson)));
        var pack = new InstalledPack(
            PackKey,
            "1.0.0",
            PackScopeTier.Horizontal,
            PackLifecycleState.Draft,
            new[] { seed },
            new Dictionary<string, int>(),
            DateTimeOffset.UtcNow,
            _key.PrincipalId,
            PackComposerRoutes.OwnRosterEpoch,
            TrustScope.OwnRoster,
            Array.Empty<PackDependencyRef>());
        _packStore.Commit(new PackInstallTransaction(
            tenant,
            pack,
            new PackInstallWatermark(PackKey, "1.0.0", new Dictionary<string, int>()),
            Array.Empty<PackTenantOverride>()));
    }

    private static object Text(string en) => new
    {
        defaultLocale = "en",
        values = new Dictionary<string, string> { ["en"] = en },
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

    private sealed class ThrowingProjectionReconciler : IPackProjectionReconciler
    {
        public void AttachProjector(IPackProjectionDispatcher projector) { }

        public void ReconcilePending(CancellationToken cancellationToken = default) =>
            throw new IOException("Simulated durable-store failure.");
    }

    private sealed class ThrowingProjectionDispatcher : IPackProjectionDispatcher
    {
        public object? Project(PackProjectionAuthority authority, CancellationToken cancellationToken = default) =>
            throw new IOException("Simulated process death before projection completed.");
    }
}
