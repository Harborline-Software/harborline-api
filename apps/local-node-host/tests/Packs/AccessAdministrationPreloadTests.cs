using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Blocks.Assets.Registry.DependencyInjection;
using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.CapabilityAdmission.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Blocks.Reports;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Packs.Dcp;
using Harborline.Api.Foundation.Packs.Export;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Audit;
using Harborline.Api.Foundation.Packs.Install.Compatibility;
using Harborline.Api.Foundation.Packs.Install.Trust;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Serialization;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Foundation.Packs.Validation;
using Harborline.Api.Foundation.Packs.Verify;
using Harborline.Api.Foundation.ReportDefinitions;
using Harborline.Api.Foundation.ViewDefinitions;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.Kernel.Schema;
using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Data.PackProjection;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Authorization;

using Xunit;
using Xunit.Abstractions;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

/// <summary>
/// Ticket 208 slice 1 acceptance: the Access administration package is preloaded from an EMPTY
/// installation through the ordinary export → install → activate → project path, creating exactly the TWO
/// definitions this host can admit (the grant form and the privileged-review workflow) with pack
/// provenance and NO grant row, and a second boot neither duplicates nor re-installs it.
/// <para>
/// Fix 1: the view and report registries here are the host's REAL
/// <see cref="HostViewKindDescriptorRegistry"/> / <see cref="HostReportKindDescriptorRegistry"/> — no
/// accept-all stub — so a green here is a green in the composed node.
/// </para>
/// </summary>
[Collection(PackProjectionBarrierCollection.Name)]
public sealed partial class AccessAdministrationPreloadTests : IAsyncLifetime
{
    private static readonly TenantId Tenant = new("aaaaaaaa-0000-0000-0000-000000000208");

    /// <summary>The canonical form field order the export document declares (fix 1, MAJOR 4).</summary>
    private static readonly string[] GrantFormFields =
    {
        "person", "role", "scope", "residency", "effectiveFrom", "effectiveTo", "reason",
    };

    private WebApplication _app = null!;
    private KeyPair _key = null!;
    private NodePrincipalSigner _signer = null!;
    private InMemoryPackInstallStore _store = null!;
    private PackInstaller _installer = null!;
    private InMemoryViewDefinitionRegistry _views = null!;
    private InMemoryRenderPlanCatalogue _renderPlans = null!;
    private AuthorizedFormDefinitionLifecycle _authorizedForms = null!;
    private InMemoryReportDefinitionRegistry _reports = null!;
    private IFormDefinitionStore _forms = null!;
    private IWorkflowDefinitionStore _workflows = null!;
    private AccessAdministrationPreloadHostedService _preload = null!;
    private PlatformPackPreloadHostedService _platformPreload = null!;
    private InMemoryRoleVocabulary _roles = null!;
    private InMemoryPackInstallAudit _audit = null!;
    private InMemoryAuthorizationConfigurationStore _configuration = null!;
    private Func<ViewDefinition, CancellationToken, ValueTask>? _beforeViewAdmission;
    private readonly ActiveCascadeDefaultsProjection _defaults = new();
    private readonly CatalogueDetailTemplates _details = new();
    private HttpClient _client = null!;
    private readonly ITestOutputHelper _output;

    public AccessAdministrationPreloadTests(ITestOutputHelper output) => _output = output;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddLogging();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddInMemoryAssetTypeSystem();
        builder.Services.AddSingleton<Harborline.Api.Foundation.Recovery.TenantKey.ITenantKeyProvider,
            Harborline.Api.Foundation.Recovery.TenantKey.InMemoryTenantKeyProvider>();
        builder.Services.AddSingleton<Harborline.Api.Foundation.Recovery.Crypto.IFieldEncryptor,
            Harborline.Api.Foundation.Recovery.Crypto.TenantKeyProviderFieldEncryptor>();
        builder.Services.AddTestAuthorizationGate().AddTestNodeForms(
            configureWriters: (services, entityMutations, _) =>
            {
                _workflowMutationAccessor = entityMutations;
                services.AddEntityStoreWorkflowDefinitionStore(entityMutations);
            });
        builder.Services.AddSingleton<IWorkflowAdmissionValidator, WorkflowAdmissionValidator>();
        _app = builder.Build();

        // The node's own signing identity: the export is signed by it and the trust store recognises it as
        // the OwnRoster root — exactly the posture the composed host gives the preload.
        _key = KeyPair.Generate();
        var seed = new byte[32];
        Random.Shared.NextBytes(seed);
        _signer = new NodePrincipalSigner(seed);

        _forms = _app.Services.GetRequiredService<IFormDefinitionStore>();
        _workflows = _app.Services.GetRequiredService<IWorkflowDefinitionStore>();
        var schemas = _app.Services.GetRequiredService<ISchemaRegistry>();
        _roles = new InMemoryRoleVocabulary(AccessGrantAuthorizationSeed.RoleDefinitions);
        var roleGate = new RoleGateAdmission(_roles, _forms, _workflows);
        var (grants, configuration) = TestInMemoryAuthorizationStores.Pair();
        _configuration = configuration;
        var authorizationWriter = new AuthorizationDefinitionWriter(configuration, configuration,
            new AuthorizationDefinitionAdmission(_roles), new AuthorizationCapabilityBindingAdmission(),
            TestAuthorization.AllowGate(), grants);
        await new AccessGrantAuthorizationSeed(authorizationWriter, configuration, grants)
            .InstallAsync(Tenant, TestAuthorization.At, AuthorizationSeedProfile.Production);
        // The REAL host descriptor registries: the shipped definitions must be admissible by the
        // composed node, not by a stub. (They admit the two shipped items because neither is a view or
        // a report; a view over an unregistered entity type or an unregistered report kind still fails.)
        _views = new InMemoryViewDefinitionRegistry(new ObservedViewAdmission(
            new HostViewKindDescriptorRegistry(
                _app.Services.GetRequiredService<IEntityTypeRegistry>(),
                _forms,
                schemas,
                Harborline.Blocks.EntityViews.ViewKindRegistry.Platform),
            (definition, ct) => _beforeViewAdmission?.Invoke(definition, ct) ?? ValueTask.CompletedTask));
        _reports = new InMemoryReportDefinitionRegistry(
            new HostReportKindDescriptorRegistry(new ReportCartridgeRegistry()));
        _renderPlans = new InMemoryRenderPlanCatalogue();
        _authorizedForms = TestAuthorization.FormLifecycle(_forms, TestAuthorization.AllowGate(), roleGate);

        _store = new InMemoryPackInstallStore();
        var codec = new PackFileCodec();
        _audit = new InMemoryPackInstallAudit();
        _installer = new PackInstaller(
            new PackVerifier(new Ed25519Verifier(), codec),
            new DiagnosticReadStore(_store, () => _beforeInstallerPackRead?.Invoke()),
            _store,
            (IPackProjectionAdmissionStore)_store,
            new PackWorkflowAdmissionAdapter(new WorkflowAdmissionValidator(), roleGateAdmission: roleGate, defaults: _defaults,
                catalogueFields: new CatalogueFieldSourceAdmission(CatalogueDetailRuntime.Supports)),
            _audit,
            TestAuthorization.AllowGate(), new PackPlatformCompatibility("1.0.0",
                [new PackProjectorCase(PackContentKind.FormDefinition, [CatalogueFieldSourceContract.CapabilityId])]));
        var projector = new PackSeedProjector(
            new DiagnosticReadStore(_store, () => _beforeDiagnosticPackRead?.Invoke()),
            _app.Services.GetRequiredService<IEntityTypeRegistry>(),
            NullLogger<PackSeedProjector>.Instance,
            forms: _forms,
            schemas: schemas,
            workflows: _workflows,
            time: TimeProvider.System,
            reportDefinitions: _reports,
            viewDefinitions: _views,
            authorizedForms: _authorizedForms,
            authorizedWorkflows: TestAuthorization.WorkflowLifecycle(_workflows, TestAuthorization.AllowGate(), roleGate),
            roleVocabulary: _roles,
            authorizationDefinitions: authorizationWriter,
            authorizationDefinitionCatalogue: configuration,
            workflowLintReports: _workflowLintReports,
            viewReachabilityReports: _viewReachabilityReports,
            edgeIndex: new DeferredHealthRefreshProbe(
                new Harborline.Api.Foundation.Packs.Graph.InMemoryPackContentEdgeIndexProvider(_store),
                () => _beforeDiagnosticRefresh?.Invoke()),
            renderPlans: _renderPlans,
            defaults: _defaults, catalogueFields: new CatalogueFieldSourceAdmission(CatalogueDetailRuntime.Supports), catalogueDetails: _details);
        ((IPackProjectionReconciler)_installer).AttachProjector(projector);

        _preload = new AccessAdministrationPreloadHostedService(
            new PackExporter(
                new PackContentCanonicalizer(),
                new PackDcpCanonicalizer(),
                new PackValidator(new PackContentPiiScanner()),
                new DcpValidator(DcpCounselRegister.FromEmbeddedResource()),
                codec,
                timeProvider: TimeProvider.System),
            _signer,
            _installer,
            _store,
            TrustingTheNodeKey(),
            PackRevocationList.Empty,
            new NoActiveTeam(),
            TimeProvider.System,
            NullLogger<AccessAdministrationPreloadHostedService>.Instance);

        _platformPreload = new PlatformPackPreloadHostedService(
            new PackExporter(
                new PackContentCanonicalizer(),
                new PackDcpCanonicalizer(),
                new PackValidator(new PackContentPiiScanner()),
                new DcpValidator(DcpCounselRegister.FromEmbeddedResource()),
                codec,
                timeProvider: TimeProvider.System),
            _signer,
            _installer,
            _store,
            TrustingTheNodeKey(),
            PackRevocationList.Empty,
            new NoActiveTeam(),
            TimeProvider.System,
            NullLogger<PlatformPackPreloadHostedService>.Instance);

        _app.Use(async (http, next) =>
        {
            http.Features.Set(DesktopPlaneRequestFeature.Instance);
            http.Features.Set(new SelectedSessionRequestPrincipal(
                "platform-seed-account", Tenant, new PrincipalUserId("platform-seed-principal"),
                new CanonicalPartyReference("platform-seed-party"), "platform-seed-membership", 1,
                [new PinnedGrantOwnerVersion("platform-seed-grant", 1)], 1,
                "platform-seed-session", "platform-seed-coordination"));
            await next(http);
        });
        var catalogue = new ProjectedCatalogue(_authorizedForms, _views, _renderPlans,
            new CatalogueRegistries(_store, defaults: _defaults));
        CatalogueRoutes.Map(_app.MapSelectedSessionProductGroup(), catalogue, _store,
            new ActiveTeamTenantContext(new NoActiveTeam()));
        CatalogueDetailRoutes.Map(_app.MapSelectedSessionProductGroup(),
            new CatalogueDetailRuntime(_authorizedForms.CatalogueSources, _details, TestAuthorization.AllowGate()));
        await _app.StartAsync();
        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        _client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) };

    }

    [Fact]
    public async Task Released_platform_detail_seed_projects_exact_form_fields_and_replays_without_mutation()
    {
        await _platformPreload.PreloadAsync(Tenant, CancellationToken.None);
        var releasedAuthor = Assert.Single(
            PlatformPackPreloadHostedService.ReadExportRequest(_signer.Signer.IssuerId.ToBase64Url()).Contents,
            item => item.Key == "platform.pack.author");
        var releasedTitle = releasedAuthor.Content["overlay"]!["title"]!["values"]!["en"]!.GetValue<string>();
        Assert.Equal("Author a domain pack", releasedTitle);
        var listBody = await _client.GetFromJsonAsync<JsonElement>(
            $"{CatalogueRoutes.RouteBase}?kind=FormDefinition");
        _output.WriteLine("M6_PLATFORM_CATALOGUE_LIST=" + listBody.GetRawText());
        var listedBody = Assert.Single(listBody.GetProperty("entries").EnumerateArray(),
            entry => entry.GetProperty("id").GetString() == "platform.pack.author");
        Assert.Equal("Author a domain pack",
            listedBody.GetProperty("title").GetProperty("values").GetProperty("en").GetString());
        var listedBinding = listedBody.GetProperty("catalogueFieldBinding");
        Assert.Equal("harborline.platform",
            listedBinding.GetProperty("provenance").GetProperty("packKey").GetString());
        Assert.Equal("1.6.0",
            listedBinding.GetProperty("provenance").GetProperty("packVersion").GetString());
        var listedSourceBinding = JsonSerializer.Deserialize<CatalogueFieldSourceBinding>(listedBinding)!;
        var definition = await _forms.GetAsync(new(Tenant, "platform.detail.form", "1.0.1"));
        Assert.NotNull(definition);
        Assert.Equal("forms.catalogue-field-source", definition.CatalogueFieldSource!.CapabilityId);
        Assert.Equal(1, definition.CatalogueFieldSource.CoordinateSchemaVersion);
        Assert.Equal(1, definition.CatalogueFieldSource.SourceMappingSchemaVersion);
        Assert.Equal("FormDefinition", definition.CatalogueFieldSource.SourceKind);
        Assert.Equal(CatalogueFieldSourceContract.Fields, definition.CatalogueFieldSource!.Fields);
        Assert.Equal(new[] { "formId", "title", "version", "cascadeLayer" },
            definition.CatalogueFieldSource.Fields.Select(field => field.FieldId));
        var coordinate = new CatalogueFieldCoordinate(1, "FormDefinition", "platform.pack.author", "1.0.1", "formId");
        var authored = await _forms.GetAsync(new(Tenant, "platform.pack.author", "1.0.1"));
        Assert.Equal("en", authored.Overlay.Title?.DefaultLocale);
        Assert.Equal(releasedTitle, authored.Overlay.Title?.Values["en"]);
        var catalogue = new ProjectedCatalogue(_authorizedForms, _views, _renderPlans,
            new CatalogueRegistries(_store, defaults: _defaults));
        var listed = Assert.Single((await catalogue.ListAsync(Tenant, PackContentKind.FormDefinition)).Entries,
            entry => entry.Id == "platform.pack.author");
        Assert.Equal(releasedTitle, listed.Title?.Values["en"]);
        Assert.NotNull(listed.CatalogueFieldBinding);
        var source = _authorizedForms.CatalogueSources.Resolve(Tenant, coordinate)!;
        Assert.True(JsonElement.DeepEquals(listedBinding,
            JsonSerializer.SerializeToElement(source.Identity.Binding, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
        var request = JsonSerializer.SerializeToElement(CatalogueFieldSourceContract.Fields.Select(field =>
            new CatalogueFieldReadRequest(coordinate with { Field = field.FieldId }, listedSourceBinding)));
        using var detailResponse = await _client.PostAsJsonAsync(
            "/api/local-node/catalogue/details/platform.detail.form/1.0.1", request);
        detailResponse.EnsureSuccessStatusCode();
        var detailBody = await detailResponse.Content.ReadFromJsonAsync<JsonElement>();
        _output.WriteLine("M6_PLATFORM_DETAIL_POST=" + detailBody.GetRawText());
        var routedProjection = detailBody.GetProperty("projection");
        Assert.Equal(releasedTitle, routedProjection.GetProperty("values").GetProperty("title")
            .GetProperty("values").GetProperty("en").GetString());
        Assert.Empty(detailBody.GetProperty("refusals").EnumerateArray());
        var runtime = new CatalogueDetailRuntime(_authorizedForms.CatalogueSources, _details, TestAuthorization.AllowGate());
        var projection = await runtime.ProjectAsync("platform.detail.form", "1.0.1", request, TestAuthorization.Write(Tenant));
        Assert.Equal(new[] { "formId", "title", "version", "cascadeLayer" }, projection.Values.Keys);
        Assert.Equal("platform.pack.author", projection.Values["formId"].GetString());
        Assert.Equal(releasedTitle,
            projection.Values["title"].GetProperty("values").GetProperty("en").GetString());
        Assert.Equal("1.0.1", projection.Values["version"].GetString());
        Assert.True(projection.ReadOnly);
        Assert.Equal("1.6.0", projection.DetailBinding.Provenance.PackVersion);
        Assert.Equal("sha256:" + _renderPlans.Get(Tenant, PackContentKind.FormDefinition, "platform.detail.form", "1.0.1")!.DefinitionHash,
            projection.DetailBinding.DefinitionHash);
        await _platformPreload.PreloadAsync(Tenant, CancellationToken.None);
        Assert.Equal(JsonSerializer.Serialize(definition),
            JsonSerializer.Serialize(await _forms.GetAsync(new(Tenant, "platform.detail.form", "1.0.1"))));
        var denied = new CatalogueDetailRuntime(_authorizedForms.CatalogueSources, _details,
            TestAuthorization.Gate(decision => !decision.Target.Scope.Value.EndsWith("/title", StringComparison.Ordinal)));
        var partial = await denied.ProjectAsync("platform.detail.form", "1.0.1", request, TestAuthorization.Write(Tenant));
        Assert.DoesNotContain("title", partial.Values.Keys);
        Assert.DoesNotContain("title", partial.FieldsMeta.Keys);
        Assert.False(partial.Overlay.GetProperty("fields").TryGetProperty("title", out _));
        // The zero protected-getter-read invariant is exercised with the same runtime by
        // CatalogueFieldRuntimeTests.Denied_non_pii_title_has_zero_getter_reads_and_no_declaration_value_or_section_reference.
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        _signer.Dispose();
        _key.Dispose();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task Preload_from_an_empty_installation_activates_the_pack_and_projects_all_definitions()
    {
        await PreloadPlatformThenAccessAsync();

        var active = _store.GetActive(Tenant, AccessAdministrationPreloadHostedService.PackKey);
        Assert.NotNull(active);
        Assert.Equal(AccessAdministrationPreloadHostedService.PackVersion, active!.Version);
        Assert.Equal(PackLifecycleState.Active, active.Lifecycle);

        // T-433 adds the holder view over the host's compiled AccessGrant descriptor. It remains
        // pack-defined; the host supplies only the shared entity identity/field description.
        Assert.Equal(
            new[]
            {
                (PackContentKind.RoleDefinition, "access.admitted-user"),
                (PackContentKind.RoleDefinition, "access.form-submitter"),
                (PackContentKind.FormDefinition, "access.grant-a-role"),
                (PackContentKind.ViewDefinition, "access.holders"),
                (PackContentKind.NavWorkspaceConfig, "access.navigation"),
                (PackContentKind.WorkflowDefinition, "access.privileged-grant-review"),
            },
            active.SeedItems.Select(item => (item.Kind, item.Key)).OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray());

        // Both content keys resolve through the ordinary catalogue reads — over the REAL registries, so
        // "projected, not refused" is a claim about the composed host.
        var form = await _forms.GetAsync(
            new DefinitionCoordinates(Tenant, "access.grant-a-role", "1.0.1"), CancellationToken.None);
        Assert.NotNull(form);
        var workflow = await _workflows.GetAsync(
            new DefinitionCoordinates(Tenant, "access.privileged-grant-review", "1.0.1"), CancellationToken.None);
        Assert.NotNull(workflow);
        var holders = await _views.GetDefinitionAsync(Tenant.Value, "access.holders", "1.0.2");
        Assert.NotNull(holders);
        Assert.Equal(HostViewKindDescriptorRegistry.AccessGrantEntityType,
            holders.Parameters.GetProperty("entityType").GetString());
        var admittedRole = new RoleReference(RoleVocabularies.Domain, "admitted-user");
        Assert.NotNull(await _roles.ResolveAsync(admittedRole));
        Assert.Empty(await _configuration.DefinitionsForRoleAsync(Tenant, admittedRole));
    }

    [Fact]
    public async Task Access_preload_does_not_run_until_the_platform_pack_is_active()
    {
        await _preload.PreloadAsync(Tenant, CancellationToken.None);

        Assert.Null(_store.GetActive(Tenant, AccessAdministrationPreloadHostedService.PackKey));
        Assert.Null(_store.GetVersion(
            Tenant,
            AccessAdministrationPreloadHostedService.PackKey,
            AccessAdministrationPreloadHostedService.PackVersion));
    }

    [Fact]
    public async Task Access_activation_with_a_declared_platform_dependency_is_refused_until_platform_is_active()
    {
        var context = new PackInstallContext(
            Tenant,
            TrustingTheNodeKey(),
            PackRevocationList.Empty,
            TimeProvider.System.GetUtcNow(),
            PackInstallRoutes.RevocationMaxAge,
            Principal: AccessGrantAuthorizationSeed.NodeOperatorPrincipal);
        var platform = await ExportAsync(new PackExportRequest(
            PlatformPackPreloadHostedService.PackKey,
            PlatformPackPreloadHostedService.PackVersion,
            "Platform dependency fixture",
            "Installs the declared dependency but deliberately leaves it inactive.",
            PackScopeTier.Horizontal,
            [],
            [],
            [],
            PackComposerRoutes.OwnRosterEpoch,
            Dcp: DomainComplianceProfile.General(_signer.Signer.IssuerId.ToBase64Url())));
        Assert.True(_installer.Install(platform, context).Installed);

        // The request comes from the shipped export document. Removing its platform dependency is
        // the mutation that turns this test red: this otherwise inactive platform fixture no longer
        // invokes the declared-dependency activation refusal.
        var access = await ExportAsync(AccessAdministrationPreloadHostedService.ReadExportRequest(
            _signer.Signer.IssuerId.ToBase64Url()));
        Assert.True(_installer.Install(access, context).Installed);

        var activation = _installer.Activate(
            context,
            AccessAdministrationPreloadHostedService.PackKey,
            AccessAdministrationPreloadHostedService.PackVersion);

        Assert.False(activation.Activated);
        Assert.StartsWith(PackInstallCodes.ActivatePlatformPackRequired, activation.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task First_boot_activates_platform_then_access_and_a_second_boot_is_idempotent()
    {
        await _platformPreload.PreloadAsync(Tenant, CancellationToken.None);
        await _preload.PreloadAsync(Tenant, CancellationToken.None);

        var firstBoot = _store.ListInstalled(Tenant)
            .OrderBy(pack => pack.PackKey, StringComparer.Ordinal)
            .Select(pack => (pack.PackKey, pack.Version, pack.Lifecycle, ItemCount: pack.SeedItems.Count))
            .ToArray();
        Assert.Equal(
            new[]
            {
                (AccessAdministrationPreloadHostedService.PackKey, AccessAdministrationPreloadHostedService.PackVersion,
                    PackLifecycleState.Active, 6),
                (PlatformPackPreloadHostedService.PackKey, PlatformPackPreloadHostedService.PackVersion,
                    PackLifecycleState.Active, 64),
            },
            firstBoot);
        Assert.Equal(
            new[] { PlatformPackPreloadHostedService.PackKey, AccessAdministrationPreloadHostedService.PackKey },
            _audit.Query(Tenant)
                .Where(entry => entry.Action == PackInstallAuditAction.Activated)
                .Select(entry => entry.PackKey)
                .ToArray());

        var administrator = await _roles.ResolveAsync(RoleReference.Administrator);
        var auditor = await _roles.ResolveAsync(RoleReference.Auditor);
        Assert.NotNull(administrator);
        Assert.NotNull(auditor);
        var platform = _store.GetActive(Tenant, PlatformPackPreloadHostedService.PackKey)!;
        Assert.Equal(2, platform.SeedItems.Count(item => item.Kind == PackContentKind.RoleDefinition));
        Assert.Equal(2, platform.SeedItems.Count(item => item.Kind == PackContentKind.AuthorizationCapabilityBinding));
        Assert.Equal(39, platform.SeedItems.Count(item => item.Kind == PackContentKind.ViewDefinition));
        var projectedViews = await _views.ListDefinitionsAsync(Tenant.Value, CancellationToken.None);
        Assert.Equal(40, projectedViews.Count);
        var catalogue = new ProjectedCatalogue(_authorizedForms, _views, _renderPlans,
            new CatalogueRegistries(_store, defaults: _defaults));
        var defaults = await catalogue.ListAsync(Tenant, PackContentKind.CascadeDefaults);
        Assert.Empty(defaults.KindsUnavailable);
        var defaultEntry = Assert.Single(defaults.Entries);
        Assert.Equal("platform.defaults.pack-author", defaultEntry.Id);
        Assert.Equal(new CatalogueProvenance(platform.PackKey, platform.Version, "pack"), defaultEntry.Provenance);
        var formsView = await catalogue.GetAsync(
            Tenant, PackContentKind.ViewDefinition, "platform.list.forms", cancellationToken: CancellationToken.None);
        Assert.NotNull(formsView);
        Assert.Equal("harborline.platform", formsView!.Provenance.PackKey);
        Assert.NotNull(formsView.RenderPlan);
        Assert.Equal(formsView.DefinitionHash, formsView.RenderPlan!.DefinitionHash);
        Assert.Equal(Harborline.Blocks.EntityViews.ViewKindIds.Table,
            formsView.RenderPlan.Bindings.GetProperty("viewKind").GetString());
        Assert.Equal(7, formsView.RenderPlan.Bindings.GetProperty("actions").GetArrayLength());
        var authorForm = await catalogue.GetAsync(
            Tenant, PackContentKind.FormDefinition, "platform.pack.author", cancellationToken: CancellationToken.None);
        Assert.NotNull(authorForm?.RenderPlan);
        Assert.Equal("harborline.platform", authorForm!.Provenance.PackKey);
        Assert.Equal("textarea", authorForm.RenderPlan!.Bindings.GetProperty("fields").GetProperty("packJson").GetProperty("type").GetString());
        var workshop = Assert.Single(platform.SeedItems, item => item.Key == "platform.workshop");
        using var workshopDocument = JsonDocument.Parse(workshop.CanonicalJson);
        var group = workshopDocument.RootElement.GetProperty("seedWorkspaces")[0].GetProperty("groups")[0];
        Assert.Equal(13, group.GetProperty("itemIds").GetArrayLength());
        Assert.Equal(13, group.GetProperty("items").GetArrayLength());
        var inspector = Assert.Single(workshopDocument.RootElement.GetProperty("panelSet").EnumerateArray());
        Assert.Equal("inspector", inspector.GetProperty("id").GetString());
        Assert.Equal("panels.inspector.toggle", inspector.GetProperty("binding").GetString());
        Assert.Equal("mod+shift+i", inspector.GetProperty("shortcut").GetString());
        Assert.Equal(360, inspector.GetProperty("defaultWidth").GetInt32());
        Assert.Equal(180, inspector.GetProperty("minimumHeight").GetInt32());
        Assert.False(inspector.GetProperty("defaultOpen").GetBoolean());

        await _platformPreload.PreloadAsync(Tenant, CancellationToken.None);
        await _preload.PreloadAsync(Tenant, CancellationToken.None);

        Assert.Equal(firstBoot, _store.ListInstalled(Tenant)
            .OrderBy(pack => pack.PackKey, StringComparer.Ordinal)
            .Select(pack => (pack.PackKey, pack.Version, pack.Lifecycle, ItemCount: pack.SeedItems.Count))
            .ToArray());
        Assert.Equal(2, _store.ListInstalled(Tenant).Count);
        Assert.Equal(2, _audit.Query(Tenant).Count(entry => entry.Action == PackInstallAuditAction.Activated));
    }

    [Theory]
    [InlineData("1.1.1")]
    [InlineData("1.1.2")]
    public async Task Access_preload_upgrades_released_predecessor_without_rewriting_its_holder_definition(string previousVersion)
    {
        await _platformPreload.PreloadAsync(Tenant, CancellationToken.None);
        var bytes = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory,
            "Conformance", "Packs", "access-replacement", $"access-administration-pack-{previousVersion}.export.json"));
        Assert.Equal(previousVersion == "1.1.1"
            ? "99A3EDFA484314C7A0523F4D12B9D7F87725F7B65FC95BBC34DC7CA79E7FDCB2"
            : "392F2545710D5CB3A7DF8724E43D65E751607DEBFAFE00898D704819D55A381F",
            Convert.ToHexString(SHA256.HashData(bytes)));
        if (previousVersion == "1.1.1")
            bytes = await ExportAsync(ReadExactLegacyPlatformRequest(bytes, _signer.Signer.IssuerId.ToBase64Url())
                with { Exposes = ["access.holders"], InterfaceVersion = 1 });
        var released = new PackFileCodec().TryDecode(bytes)!;
        var trust = new InMemoryPackTrustStore([
            new PackTrustRoot(TrustScope.OwnRoster, released.Envelope!.IssuerId, 1, TrustRootStatus.Current),
        ]);
        var context = new PackInstallContext(Tenant, trust, PackRevocationList.Empty,
            TimeProvider.System.GetUtcNow(), PackInstallRoutes.RevocationMaxAge,
            Principal: AccessGrantAuthorizationSeed.NodeOperatorPrincipal);
        var installed = _installer.Install(bytes, context);
        Assert.True(installed.Installed, JsonSerializer.Serialize(installed));
        var activation = _installer.Activate(context, AccessAdministrationPreloadHostedService.PackKey, previousVersion);
        Assert.True(activation.Activated, JsonSerializer.Serialize(activation));
        var previous = _store.GetActive(Tenant, AccessAdministrationPreloadHostedService.PackKey)!;
        var previousHolder = Assert.Single(previous.SeedItems, item => item.Key == "access.holders");

        await _preload.PreloadAsync(Tenant, CancellationToken.None);

        var active = _store.GetActive(Tenant, AccessAdministrationPreloadHostedService.PackKey)!;
        Assert.Equal("1.1.3", active.Version);
        var old = _store.GetVersion(Tenant, active.PackKey, previousVersion)!;
        Assert.Equal(PackLifecycleState.Superseded, old.Lifecycle);
        Assert.Equal(previousHolder, Assert.Single(old.SeedItems, item => item.Key == "access.holders"));
        var view = await _views.GetDefinitionAsync(Tenant.Value, "access.holders", "1.0.2");
        Assert.NotNull(view);
        foreach (var action in view.Parameters.GetProperty("actions").EnumerateArray()
            .Where(action => action.GetProperty("id").GetString() is "narrow" or "revoke"))
            Assert.Equal("input", action.GetProperty("dispatch").GetProperty("bindings").GetProperty("target").GetProperty("source").GetString());
        await _preload.PreloadAsync(Tenant, CancellationToken.None);
        Assert.Equal(active, _store.GetActive(Tenant, active.PackKey));
    }

    [Fact]
    public async Task Platform_preload_upgrades_exact_released_1_3_without_rewriting_immutable_forms()
    {
        var context = new PackInstallContext(
            Tenant,
            TrustingTheNodeKey(),
            PackRevocationList.Empty,
            TimeProvider.System.GetUtcNow(),
            PackInstallRoutes.RevocationMaxAge,
            Principal: AccessGrantAuthorizationSeed.NodeOperatorPrincipal);
        var fixtureBytes = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory,
            "Conformance", "Packs", "platform", "platform-pack-1.3.0.export.json"));
        Assert.Equal("EF1308048DFC3553A912594CA3E9133222905581B64A315D709A743A3F24E1C4",
            Convert.ToHexString(SHA256.HashData(fixtureBytes)));
        var legacy = ReadExactLegacyPlatformRequest(fixtureBytes, _signer.Signer.IssuerId.ToBase64Url());
        Assert.Equal("1.3.0", legacy.Version);
        var legacyAuthor = Assert.Single(legacy.Contents, item => item.Key == "platform.pack.author");
        var legacyDetail = Assert.Single(legacy.Contents, item => item.Key == "platform.detail.form");
        Assert.Equal("1.0.0", legacyAuthor.Version);
        Assert.Equal("1.0.0", legacyDetail.Version);
        Assert.Equal("Literal", legacyAuthor.Content["overlay"]!["title"]!["kind"]!.GetValue<string>());
        Assert.Equal("Literal", legacyDetail.Content["overlay"]!["title"]!["kind"]!.GetValue<string>());
        var legacyBytes = await ExportAsync(legacy);
        Assert.True(_installer.Install(legacyBytes, context).Installed);
        var activation = _installer.Activate(context, legacy.Key, legacy.Version);
        Assert.True(activation.Activated, JsonSerializer.Serialize(activation));
        var installedLegacy = _store.GetVersion(Tenant, legacy.Key, legacy.Version)!;
        var immutableItems = installedLegacy.SeedItems
            .Where(item => item.Kind == PackContentKind.FormDefinition)
            .ToDictionary(item => item.Key, item => (item.Version, item.ContentAddress, item.CanonicalJson), StringComparer.Ordinal);
        var oldDetail = Assert.IsType<FormDefinition>(
            await _forms.GetAsync(new(Tenant, "platform.detail.form", "1.0.0")));
        var oldAuthor = Assert.IsType<FormDefinition>(
            await _forms.GetAsync(new(Tenant, "platform.pack.author", "1.0.0")));
        var oldDetailSemantics = ImmutableFormSemantics(oldDetail);
        var oldAuthorSemantics = ImmutableFormSemantics(oldAuthor);

        await _platformPreload.PreloadAsync(Tenant, CancellationToken.None);

        var active = _store.GetActive(Tenant, PlatformPackPreloadHostedService.PackKey)!;
        Assert.Equal("1.6.0", active.Version);
        Assert.Equal(PackLifecycleState.Active, active.Lifecycle);
        var preservedLegacy = _store.GetVersion(Tenant, legacy.Key, "1.3.0")!;
        Assert.Equal(PackLifecycleState.Superseded, preservedLegacy.Lifecycle);
        var preservedItems = preservedLegacy.SeedItems
            .Where(item => item.Kind == PackContentKind.FormDefinition)
            .ToDictionary(item => item.Key, item => (item.Version, item.ContentAddress, item.CanonicalJson), StringComparer.Ordinal);
        Assert.Equal(immutableItems.Count, preservedItems.Count);
        Assert.Equal(immutableItems.Keys.Order(StringComparer.Ordinal), preservedItems.Keys.Order(StringComparer.Ordinal));
        foreach (var item in immutableItems)
            Assert.Equal(item.Value, preservedItems[item.Key]);
        var preservedDetail = Assert.IsType<FormDefinition>(
            await _forms.GetAsync(new(Tenant, "platform.detail.form", "1.0.0")));
        var preservedAuthor = Assert.IsType<FormDefinition>(
            await _forms.GetAsync(new(Tenant, "platform.pack.author", "1.0.0")));
        Assert.Equal(FormDefinitionStatus.Withdrawn, preservedDetail.Status);
        Assert.Equal(FormDefinitionStatus.Withdrawn, preservedAuthor.Status);
        Assert.Equal(oldDetailSemantics, ImmutableFormSemantics(preservedDetail));
        Assert.Equal(oldAuthorSemantics, ImmutableFormSemantics(preservedAuthor));
        var newDetail = await _forms.GetAsync(new(Tenant, "platform.detail.form", "1.0.1"));
        var newAuthor = await _forms.GetAsync(new(Tenant, "platform.pack.author", "1.0.1"));
        Assert.Equal(FormDefinitionStatus.Published, newDetail.Status);
        Assert.Equal(FormDefinitionStatus.Published, newAuthor.Status);
        Assert.Equal("Form details", newDetail.Overlay.Title!.Values["en"]);
        Assert.Equal("Author a domain pack", newAuthor.Overlay.Title!.Values["en"]);
        Assert.Equal("1.6.0", Assert.Single(_defaults.List(Tenant)).Source.PackVersion);
        Assert.Equal(39, active.SeedItems.Count(item => item.Kind == PackContentKind.ViewDefinition));
        var projected = await _views.ListDefinitionsAsync(Tenant.Value, CancellationToken.None);
        Assert.Equal(39, projected.Count);
        Assert.Equal(13, projected.Count(view => view.Key.StartsWith("platform.health.", StringComparison.Ordinal)));
        Assert.Equal(13, projected.Count(view => view.Key.StartsWith("platform.browse.", StringComparison.Ordinal)));
        Assert.Equal(
            new[] { "1.3.0", "1.6.0" },
            _store.ListInstalled(Tenant)
                .Where(pack => pack.PackKey == PlatformPackPreloadHostedService.PackKey)
                .Select(pack => pack.Version)
                .Order(StringComparer.Ordinal)
                .ToArray());
    }

    private static string ImmutableFormSemantics(FormDefinition definition) => JsonSerializer.Serialize(new
    {
        definition.Envelope,
        definition.SchemaRef,
        definition.Overlay,
        definition.CatalogueFieldSource,
        definition.CreatedAt,
        definition.PackSource,
    });

    [Fact]
    public async Task Platform_preload_carries_the_one_compiled_descriptor_for_every_record_type()
    {
        await _platformPreload.PreloadAsync(Tenant, CancellationToken.None);

        var platform = _store.GetActive(Tenant, PlatformPackPreloadHostedService.PackKey)!;
        var carried = platform.SeedItems
            .Where(item => item.Kind == PackContentKind.RecordType)
            .ToArray();
        var compiledByName = SystemRecordType.All.ToDictionary(type => type.Name, StringComparer.Ordinal);

        Assert.Equal(SystemRecordType.All.Count, carried.Length);
        Assert.Equal(SystemRecordType.All.Count, compiledByName.Count);
        foreach (var item in carried)
        {
            Assert.True(PackSealedSystemTypeAdmission.TryResolveCompiledDescriptor(item.Key, out var descriptor),
                $"Carried RecordType key '{item.Key}' does not resolve to a compiled catalogue descriptor.");
            Assert.Same(compiledByName[item.Key], descriptor);
            Assert.True(descriptor!.Sealed);
            Assert.Equal("platform", descriptor.Provenance.Kind);
        }

        var carriedKeys = carried.Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
        var compiledKeys = SystemRecordType.All.Select(type => type.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(compiledKeys.Count, carriedKeys.Count);
        Assert.Empty(carriedKeys.Except(compiledKeys));
        Assert.Empty(compiledKeys.Except(carriedKeys));
    }

    [Fact]
    public async Task A_signature_the_composed_trust_store_does_not_recognise_is_refused()
    {
        // The preload built over a trust store that roots trust in SOMEONE ELSE's key: the node's own
        // signature is then untrusted, and no package may be preloaded on its strength.
        var stranger = new NodePrincipalSigner(RandomSeed());
        try
        {
            await _platformPreload.PreloadAsync(Tenant, CancellationToken.None);
            var preload = Build(new InMemoryPackTrustStore(new[]
            {
                new PackTrustRoot(
                    TrustScope.OwnRoster,
                    stranger.Signer.IssuerId,
                    PackComposerRoutes.OwnRosterEpoch,
                    TrustRootStatus.Current),
            }));

            await preload.PreloadAsync(Tenant, CancellationToken.None);

            Assert.Null(_store.GetActive(Tenant, AccessAdministrationPreloadHostedService.PackKey));
            Assert.Single(_store.ListInstalled(Tenant), pack =>
                pack.PackKey == PlatformPackPreloadHostedService.PackKey);
        }
        finally
        {
            stranger.Dispose();
        }
    }

    [Fact]
    public async Task A_refused_definition_leaves_no_active_package_and_the_next_boot_retries()
    {
        // A host that refuses one shipped definition (here: every workflow) must not end up ACTIVE over a
        // partial projection.
        await _platformPreload.PreloadAsync(Tenant, CancellationToken.None);
        var refusing = Build(TrustingTheNodeKey(), refuseWorkflows: true);
        await refusing.PreloadAsync(Tenant, CancellationToken.None);

        Assert.Null(_store.GetActive(Tenant, AccessAdministrationPreloadHostedService.PackKey));
        Assert.NotNull(PackNavigationRoutes.PackNavigationComposer.Compose(_store, Tenant).Pack);
        // ... but the refusal is durable and operator-visible: the version is installed and INACTIVE in
        // the ordinary installed-pack listing, not silently gone.
        var pending = Assert.Single(
            _store.ListInstalled(Tenant),
            pack => pack.PackKey == AccessAdministrationPreloadHostedService.PackKey);
        Assert.NotEqual(PackLifecycleState.Active, pending.Lifecycle);
        // Staged form publication is discarded, not published and withdrawn as compensation.
        Assert.Null(await _forms.GetCurrentPublishedAsync(
            new DefinitionAddress(Tenant, "access.grant-a-role"), CancellationToken.None));

        // The next boot — the host now admitting what it refused — retries from that pending state and
        // finishes the activation without re-installing.
        await _preload.PreloadAsync(Tenant, CancellationToken.None);

        var active = _store.GetActive(Tenant, AccessAdministrationPreloadHostedService.PackKey);
        Assert.NotNull(active);
        Assert.Equal(PackLifecycleState.Active, active!.Lifecycle);
        Assert.NotNull(await _workflows.GetAsync(
            new DefinitionCoordinates(Tenant, "access.privileged-grant-review", "1.0.1"), CancellationToken.None));
    }

    [Fact]
    public void The_grant_form_maps_field_for_field_onto_the_grant_writer()
    {
        var fields = ReadGrantFormFieldsMeta();
        Assert.Equal(GrantFormFields, fields.Keys.OrderBy(k => k, StringComparer.Ordinal)
            .OrderBy(k => Array.IndexOf(GrantFormFields, k)).ToArray());

        // The two structured choices are exactly the vocabularies the writer's types accept.
        Assert.All(fields["reason"].Options!, code => Assert.Contains(code, GrantReasonCodes.All));
        Assert.All(
            fields["residency"].Options!,
            value => Assert.True(Enum.TryParse<GrantResidency>(
                value.Replace("-", string.Empty, StringComparison.Ordinal), ignoreCase: true, out _)));

        // One submitted row, read BY THE FORM'S OWN FIELD NAMES — no mapping table, no renaming.
        var row = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["person"] = "person:ada",
            ["role"] = "administrator",
            ["scope"] = "/",
            ["residency"] = "online-only",
            ["effectiveFrom"] = "2026-09-07T00:00:00Z",
            ["effectiveTo"] = "2027-09-07T00:00:00Z",
            ["reason"] = "manual",
        };
        Assert.All(
            fields.Where(field => field.Value.Required),
            field => Assert.False(string.IsNullOrWhiteSpace(row[field.Key])));

        var granter = new ActorId("person:grace");
        var now = DateTimeOffset.Parse(row["effectiveFrom"]!, CultureInfo.InvariantCulture);
        var grant = new AccessGrant(
            GrantId.New(),
            Tenant,
            subject: new ActorId(row["person"]!),
            role: new RoleReference(RoleVocabularies.Platform, row["role"]!),
            scope: new ScopeExpression(row["scope"]!),
            residency: Enum.Parse<GrantResidency>(
                row["residency"]!.Replace("-", string.Empty, StringComparison.Ordinal), ignoreCase: true),
            validity: new GrantValidity(
                now, DateTimeOffset.Parse(row["effectiveTo"]!, CultureInfo.InvariantCulture)),
            granterKind: GranterKind.Person,
            grantedBy: granter,
            grantedAt: now,
            grant: new GrantProvenance(GrantSourceKind.Manual, new GrantReason(row["reason"]!), granter),
            lastReviewedAt: now);

        Assert.Equal(GrantResidency.OnlineOnly, grant.Residency);
        Assert.Equal("manual", grant.Grant.Reason.Code);
        Assert.True(grant.IsActiveAt(now));
    }

    [Fact]
    public async Task The_preloaded_package_carries_no_grant_row()
    {
        await PreloadPlatformThenAccessAsync();

        var active = _store.GetActive(Tenant, AccessAdministrationPreloadHostedService.PackKey)!;
        // A grant is what a tenant HAS: no content item may be one, by kind or by shape.
        foreach (var item in active.SeedItems)
        {
            Assert.DoesNotContain(item.CanonicalJson, "granterId", StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(item.CanonicalJson, "grantedTo", StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(item.CanonicalJson, "accessGrant", StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task A_second_boot_neither_duplicates_nor_reinstalls_the_package()
    {
        await PreloadPlatformThenAccessAsync();
        var installedAfterFirstBoot = _store.ListInstalled(Tenant)
            .Select(pack => (pack.PackKey, pack.Version, pack.Lifecycle))
            .ToArray();

        await PreloadPlatformThenAccessAsync();

        Assert.Equal(
            installedAfterFirstBoot,
            _store.ListInstalled(Tenant).Select(pack => (pack.PackKey, pack.Version, pack.Lifecycle)).ToArray());
        Assert.Equal(2, _store.ListInstalled(Tenant).Count);
    }

    private sealed class ObservedViewAdmission(
        IViewDefinitionDescriptorRegistry inner,
        Func<ViewDefinition, CancellationToken, ValueTask> before) : IViewDefinitionDescriptorRegistry
    {
        public async ValueTask AdmitAsync(ViewDefinition definition, CancellationToken cancellationToken = default)
        {
            await before(definition, cancellationToken);
            await inner.AdmitAsync(definition, cancellationToken);
        }
    }

    private async Task PreloadPlatformThenAccessAsync()
    {
        await _platformPreload.PreloadAsync(Tenant, CancellationToken.None);
        await _preload.PreloadAsync(Tenant, CancellationToken.None);
    }

    private static PackExportRequest ReadExactLegacyPlatformRequest(byte[] bytes, string authoringPrincipal)
    {
        var document = JsonSerializer.Deserialize<ExportPackRequestDto>(bytes,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        return new PackExportRequest(
            document.Key,
            document.Version,
            document.Name ?? document.Key,
            document.Description ?? string.Empty,
            Enum.Parse<PackScopeTier>(document.ScopeTier, true),
            (document.Contents ?? []).Select(item => new PackContentSource(
                item.Key,
                Enum.Parse<PackContentKind>(item.Kind, true),
                item.Version,
                JsonNode.Parse(item.Content!.Value.GetRawText())!)).ToArray(),
            (document.Dependencies ?? []).Select(dependency => new PackDependencyRef(
                dependency.Key, dependency.Version, dependency.DeclaredDependencyKeys ?? [])).ToArray(),
            document.CapabilityRequirements ?? [],
            PackComposerRoutes.OwnRosterEpoch,
            Dcp: DomainComplianceProfile.General(authoringPrincipal));
    }

    private async Task<byte[]> ExportAsync(PackExportRequest request)
    {
        var exporter = new PackExporter(
            new PackContentCanonicalizer(),
            new PackDcpCanonicalizer(),
            new PackValidator(new PackContentPiiScanner()),
            new DcpValidator(DcpCounselRegister.FromEmbeddedResource()),
            new PackFileCodec(),
            timeProvider: TimeProvider.System);
        var exported = await exporter.ExportAsync(request, _signer.Signer, CancellationToken.None);
        Assert.True(exported.Succeeded, string.Join(",", exported.Validation.Errors.Select(error => error.Code)));
        return exported.FileBytes!;
    }

    /// <summary>The composed node's posture: the node's own key is the current own-roster root.</summary>
    private IPackTrustStore TrustingTheNodeKey() => new InMemoryPackTrustStore(new[]
    {
        new PackTrustRoot(
            TrustScope.OwnRoster,
            _signer.Signer.IssuerId,
            PackComposerRoutes.OwnRosterEpoch,
            TrustRootStatus.Current),
    });

    private static byte[] RandomSeed()
    {
        var seed = new byte[32];
        Random.Shared.NextBytes(seed);
        return seed;
    }

    /// <summary>A second preload over the same store, optionally over a projector that refuses the
    /// shipped workflow — the "this host cannot admit one of the definitions" case.</summary>
    private AccessAdministrationPreloadHostedService Build(
        IPackTrustStore trustStore, bool refuseWorkflows = false)
    {
        var installer = _installer;
        if (refuseWorkflows)
        {
            var roleGate = new RoleGateAdmission(_roles, _forms, _workflows);
            var codec = new PackFileCodec();
            installer = new PackInstaller(
                new PackVerifier(new Ed25519Verifier(), codec),
                _store,
                new PackWorkflowAdmissionAdapter(new WorkflowAdmissionValidator(), roleGateAdmission: roleGate, defaults: _defaults),
                new InMemoryPackInstallAudit(),
                TestAuthorization.AllowGate());
            ((IPackProjectionReconciler)installer).AttachProjector(new PackSeedProjector(
                _store,
                _app.Services.GetRequiredService<IEntityTypeRegistry>(),
                NullLogger<PackSeedProjector>.Instance,
                forms: _forms,
                schemas: _app.Services.GetRequiredService<ISchemaRegistry>(),
                workflows: _workflows,
                time: TimeProvider.System,
                reportDefinitions: _reports,
                viewDefinitions: _views,
                authorizedForms: TestAuthorization.FormLifecycle(_forms, TestAuthorization.AllowGate(), roleGate),
                // The one difference: the workflow writer's role gate refuses, so the workflow is REFUSED
                // by projection while the form projects — a partial projection, which must not stand.
                authorizedWorkflows: TestAuthorization.WorkflowLifecycle(
                    _workflows, TestAuthorization.AllowGate(), new RefusingRoleGate()),
                roleVocabulary: _roles, defaults: _defaults));
        }

        return new AccessAdministrationPreloadHostedService(
            new PackExporter(
                new PackContentCanonicalizer(),
                new PackDcpCanonicalizer(),
                new PackValidator(new PackContentPiiScanner()),
                new DcpValidator(DcpCounselRegister.FromEmbeddedResource()),
                new PackFileCodec(),
                timeProvider: TimeProvider.System),
            _signer,
            installer,
            _store,
            trustStore,
            PackRevocationList.Empty,
            new NoActiveTeam(),
            TimeProvider.System,
            NullLogger<AccessAdministrationPreloadHostedService>.Instance);
    }

    /// <summary>The committed export document's grant-form field metadata, read where it is shipped.</summary>
    private static IReadOnlyDictionary<string, FormFieldMeta> ReadGrantFormFieldsMeta()
    {
        using var stream = typeof(AccessAdministrationPreloadHostedService).Assembly
            .GetManifestResourceStream(
                "Harborline.Api.LocalNodeHost.Packs.access-administration-pack.export.json")!;
        using var document = JsonDocument.Parse(stream);
        var form = document.RootElement.GetProperty("contents").EnumerateArray()
            .Single(item => item.GetProperty("key").GetString() == "access.grant-a-role");
        return form.GetProperty("content").GetProperty("fieldsMeta").EnumerateObject().ToDictionary(
            field => field.Name,
            field => new FormFieldMeta(
                field.Value.GetProperty("type").GetString()!,
                field.Value.GetProperty("required").GetBoolean(),
                field.Value.GetProperty("options").ValueKind == JsonValueKind.Array
                    ? field.Value.GetProperty("options").EnumerateArray().Select(o => o.GetString()!).ToArray()
                    : null),
            StringComparer.Ordinal);
    }

    private sealed record FormFieldMeta(string Type, bool Required, IReadOnlyList<string>? Options);

    /// <summary>A role gate that refuses every gated definition (the host-cannot-admit-it stand-in).</summary>
    private sealed class RefusingRoleGate : IRoleGateAdmission
    {
        public ValueTask AdmitAsync(RoleGatedDefinition definition, CancellationToken ct = default)
            => throw new RoleGateAdmissionException(new RoleGateFinding(
                "role_gate.unknown_role", "workflow", definition.DefinitionId, definition.Version,
                "approve", "unknown", "role", RoleGatedDefinitionOwnerKind.VendorPackage, null, "tenant"));

        public ValueTask<IReadOnlyList<RoleGateFinding>> InspectActiveAsync(CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<RoleGateFinding>>(Array.Empty<RoleGateFinding>());
    }

    private sealed class NoActiveTeam : IActiveTeamAccessor
    {
        public TeamContext? Active => null;

        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;

#pragma warning disable CS0067
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged;
#pragma warning restore CS0067
    }
}
