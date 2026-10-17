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
using Harborline.Api.Blocks.Banking.Feed;
using Harborline.Api.Blocks.FinancialLedger.Services;
using Harborline.Api.Blocks.FinancialPeriods.Services;
using Harborline.Api.Blocks.Reports;
using Harborline.Api.Blocks.Reports.Cartridges.TrialBalance;
using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.DataExchangeDefinitions;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Documents.Issuance;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.Forms.Models;
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
using Harborline.Api.Foundation.ScheduleDefinitions;
using Harborline.Api.Foundation.Taxonomy.Models;
using Harborline.Api.Foundation.Taxonomy.Services;
using Harborline.Api.Foundation.ViewDefinitions;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.Kernel.Schema;
using Harborline.Api.LocalNodeHost.Data.PackProjection;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Authorization;
using Harborline.Blocks.BuilderDefinitions;
using Harborline.Foundation.Definitions;

using NSubstitute;
using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

/// <summary>
/// T-572 slice 4 (DES-0029 kernel-core-ck-8, DES-0014 C10): <c>POST /packs/install</c> refuses a Layout,
/// Resource or Bookable item whose declared envelope contract is outside the platform seed's window with
/// 422, in the C3 shape at stage <c>install</c>, and reports the window in DES-0006 §1's <c>contract</c>
/// shape. The seed window is app contract 1.0 over majors [1, 1] (T-572 owner ruling Q5), so the cases
/// below are both window edges: major 0 is below the oldest major, 1.0 is the newest admitted
/// declaration, 1.1 is a newer minor (T-724 ruling 86) and 2.0 a newer major.
/// </summary>
public sealed class PackContractWindowInstallRouteTests : IAsyncLifetime
{
    private const string OutOfWindow = "definition.contract.out_of_window";
    private static readonly TeamId TeamA = new(Guid.Parse("aaaa0000-0000-0000-0000-0000000005c4"));

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private KeyPair _key = null!;
    private InMemoryPackInstallStore _store = null!;
    private InMemoryFormDefinitionStore _forms = null!;
    private InMemoryTaxonomyRegistry _taxonomies = null!;
    private InMemoryReportDefinitionRegistry _reports = null!;
    private InMemoryViewDefinitionRegistry _views = null!;
    private InMemoryScheduleDefinitionRegistry _schedules = null!;
    private InMemoryDataExchangeDefinitionRegistry _exchanges = null!;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        Harborline.Api.LocalNodeHost.Tests.Authorization.TestDesktopOperator.AddTestDesktopOperator(builder.Services);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _app = builder.Build();

        _key = KeyPair.Generate();
        var codec = new PackFileCodec();
        var exporter = new PackExporter(
            new PackContentCanonicalizer(), new PackDcpCanonicalizer(),
            new PackValidator(new PackContentPiiScanner()),
            new DcpValidator(DcpCounselRegister.FromEmbeddedResource()), codec, timeProvider: TimeProvider.System);
        var verifier = new PackVerifier(new Ed25519Verifier(), codec);
        var trustStore = new InMemoryPackTrustStore(new[]
        {
            new PackTrustRoot(TrustScope.OwnRoster, _key.PrincipalId, PackComposerRoutes.OwnRosterEpoch, TrustRootStatus.Current),
        });
        var activeTeam = new MutableActiveTeamAccessor(new TeamContext(
            TeamA, "Team A", new ServiceCollection().BuildServiceProvider(), TimeProvider.System));

        _store = new InMemoryPackInstallStore();
        var installer = new PackInstaller(
            verifier,
            _store,
            new PackWorkflowAdmissionAdapter(new WorkflowAdmissionValidator()),
            new InMemoryPackInstallAudit(),
            Harborline.Api.LocalNodeHost.Tests.Authorization.TestAuthorization.AllowGate());
        var registryServices = new ServiceCollection().AddLogging().AddInMemoryAssetTypeSystem().BuildServiceProvider();
        var types = registryServices.GetRequiredService<IEntityTypeRegistry>();
        _forms = new InMemoryFormDefinitionStore(TimeProvider.System);
        var schemas = new InMemorySchemaRegistry(TimeProvider.System);
        _taxonomies = new InMemoryTaxonomyRegistry(TimeProvider.System);
        var cartridges = new ReportCartridgeRegistry();
        cartridges.Register(new TrialBalanceCartridge(Substitute.For<IChartRepository>(), Substitute.For<IAccountResolver>(),
            Substitute.For<IFiscalPeriodRepository>(), Substitute.For<IGeneralLedgerReadModel>()));
        _reports = new InMemoryReportDefinitionRegistry(new HostReportKindDescriptorRegistry(cartridges));
        _views = new InMemoryViewDefinitionRegistry(new HostViewKindDescriptorRegistry(
            types, _forms, schemas, Harborline.Blocks.EntityViews.ViewKindRegistry.Platform));
        _schedules = new InMemoryScheduleDefinitionRegistry(new HostScheduleKindDescriptorRegistry());
        _exchanges = new InMemoryDataExchangeDefinitionRegistry(
            new HostDataExchangeKindDescriptorRegistry([], Substitute.For<IBankFeedProvider>()));
        var projector = new PackSeedProjector(_store, types, NullLogger<PackSeedProjector>.Instance,
            templates: new InMemoryDocumentTemplateRegistry(), forms: _forms, schemas: schemas,
            taxonomies: _taxonomies, reportDefinitions: _reports, viewDefinitions: _views,
            scheduleDefinitions: _schedules, dataExchangeDefinitions: _exchanges,
            authorizedForms: TestAuthorization.FormLifecycle(_forms, TestAuthorization.AllowGate(), TestAuthorization.RoleGate()),
            time: TimeProvider.System);
        var authz = TestPackGate.AllowAll();
        _app.Use(async (http, next) =>
        {
            http.Features.Set(DesktopPlaneRequestFeature.Instance);
            await next(http);
        });
        PackComposerRoutes.Map(_app, exporter, verifier, trustStore, new Ed25519Signer(_key), activeTeam, authz,
            TimeProvider.System, NullLogger.Instance);
        PackInstallRoutes.Map(_app, installer, _store, trustStore, PackRevocationList.Empty, activeTeam,
            authz, TimeProvider.System, NullLogger.Instance, authorizingPrincipal: _key.PrincipalId.ToBase64Url(),
            projector: projector);

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

    [Theory(DisplayName = "T-572 S4: install refuses a platform-kind item whose contract is outside the window with 422 at stage install")]
    [Trait("Holds", "kernel-core-ck-8")]
    [InlineData("Layout", 2, 0)]
    [InlineData("Layout", 1, 1)]
    [InlineData("Layout", 0, 0)]
    [InlineData("Layout", 0, 9)]
    [InlineData("Resource", 2, 0)]
    [InlineData("Bookable", 1, 1)]
    public async Task Install_refuses_an_item_whose_contract_is_outside_the_window(string kind, int major, int minor)
    {
        using var response = await PostBytesAsync(PackInstallRoutes.InstallRoute,
            await ExportAsync(PackBody(kind, new { major, minor })));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("installed").GetBoolean());
        Assert.Equal("install", body.GetProperty("stage").GetString());
        Assert.Equal([OutOfWindow], body.GetProperty("refusalCodes").EnumerateArray().Select(code => code.GetString()));
        var refusal = Assert.Single(body.GetProperty("refusals").EnumerateArray());
        Assert.Equal(OutOfWindow, refusal.GetProperty("code").GetString());
        Assert.Equal("/envelope/contract", refusal.GetProperty("pointer").GetString());
        Assert.Equal("acme.surface@1.0.0", refusal.GetProperty("target").GetString());
        AssertReportsTheSeedWindow(body);
        Assert.Empty(_store.ListInstalled(NodeTenantFor()));
    }

    [Theory(DisplayName = "T-572 S4: install admits a platform-kind item declaring the current app contract, the window's newest edge")]
    [Trait("Holds", "kernel-core-ck-8")]
    [InlineData("Layout")]
    [InlineData("Resource")]
    [InlineData("Bookable")]
    public async Task Install_admits_an_item_declaring_the_current_app_contract(string kind)
    {
        var window = PlatformPackageSeed.ContractWindow;

        using var response = await PostBytesAsync(PackInstallRoutes.InstallRoute,
            await ExportAsync(PackBody(kind, new { major = window.Major, minor = window.Minor })));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(_store.ListInstalled(NodeTenantFor()), pack => pack.PackKey == "acme.contract");
    }

    /// <summary>
    /// Pins today's interim behaviour: an item with no <c>envelope.contract</c> still installs. This is a
    /// staged rollout, not a tolerated-missing policy (T-572 Q4 and Q6 keep "no grace period"), and the
    /// missing-declaration half of ck-8 is not Holds until the follow-up lands. That follow-up refuses
    /// <c>definition.contract.missing</c> at install and render once the authoring surfaces stamp the
    /// contract and the shipped templates declare <c>{major: 1, minor: 0}</c>, and it deletes this test.
    /// </summary>
    [Theory(DisplayName = "T-572 S4: interim, an item with no envelope contract still installs (deleted by the definition.contract.missing follow-up)")]
    [InlineData("Layout")]
    [InlineData("Resource")]
    [InlineData("Bookable")]
    public async Task Interim_install_admits_an_item_with_no_declared_contract(string kind)
    {
        using var response = await PostBytesAsync(PackInstallRoutes.InstallRoute,
            await ExportAsync(PackBody(kind, contract: null)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(_store.ListInstalled(NodeTenantFor()), pack => pack.PackKey == "acme.contract");
    }

    [Fact(DisplayName = "T-572 S4: check collects the contract refusal with the pack's other refusals and installs nothing")]
    [Trait("Holds", "kernel-core-ck-8")]
    public async Task Check_collects_the_contract_refusal()
    {
        // Check is exposed once something is installed.
        var window = PlatformPackageSeed.ContractWindow;
        using (var seed = await PostBytesAsync(PackInstallRoutes.InstallRoute, await ExportAsync(
            PackBody("Layout", new { major = window.Major, minor = window.Minor }, key: "acme.seed"))))
        {
            Assert.Equal(HttpStatusCode.OK, seed.StatusCode);
        }

        using var response = await PostBytesAsync(PackInstallRoutes.CheckRoute,
            await ExportAsync(PackBody("Layout", new { major = window.Major + 1, minor = 0 })));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Refused", body.GetProperty("verdict").GetString());
        var refusal = Assert.Single(body.GetProperty("refusals").EnumerateArray());
        Assert.Equal(OutOfWindow, refusal.GetProperty("code").GetString());
        Assert.Equal("acme.surface@1.0.0", refusal.GetProperty("target").GetString());
        Assert.DoesNotContain(_store.ListInstalled(NodeTenantFor()), pack => pack.PackKey == "acme.contract");
    }

    [Theory(DisplayName = "T-1033: install refuses a valid flat template's declared out-of-window contract")]
    [InlineData(2, 0)]
    [InlineData(1, 1)]
    public async Task Install_refuses_a_flat_template_declaring_an_out_of_window_contract(int major, int minor)
    {
        var content = TemplateContent(new JsonObject { ["major"] = major, ["minor"] = minor });
        Assert.True(PackTemplateContent.TryParse(content, out var template, out var error), error);
        Assert.Equal("acme.invoice", template.Key);
        Assert.Equal("1.0.0", template.Version);
        Assert.Equal(new DefinitionContractVersion(major, minor), template.Envelope.Contract);

        using var response = await PostBytesAsync(PackInstallRoutes.InstallRoute,
            await ExportAsync(TemplatePackBody(content)));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("installed").GetBoolean());
        Assert.Equal("install", body.GetProperty("stage").GetString());
        Assert.Equal([OutOfWindow], body.GetProperty("refusalCodes").EnumerateArray().Select(code => code.GetString()));
        var refusal = Assert.Single(body.GetProperty("refusals").EnumerateArray());
        Assert.Equal(OutOfWindow, refusal.GetProperty("code").GetString());
        Assert.Equal("/envelope/contract", refusal.GetProperty("pointer").GetString());
        Assert.Equal("acme.invoice@1.0.0", refusal.GetProperty("target").GetString());
        var contract = body.GetProperty("contract");
        Assert.Equal(1, contract.GetProperty("app_major").GetInt32());
        Assert.Equal(0, contract.GetProperty("app_minor").GetInt32());
        Assert.Equal(new[] { 1, 1 }, contract.GetProperty("window").EnumerateArray().Select(edge => edge.GetInt32()));
        Assert.Empty(_store.ListInstalled(NodeTenantFor()));
    }

    [Fact(DisplayName = "T-1033: install admits a valid flat template declaring literal contract 1.0")]
    public async Task Install_admits_a_flat_template_declaring_contract_one_zero()
    {
        var content = TemplateContent(new JsonObject { ["major"] = 1, ["minor"] = 0 });
        Assert.True(PackTemplateContent.TryParse(content, out var template, out var error), error);
        Assert.Equal(new DefinitionContractVersion(1, 0), template.Envelope.Contract);

        using var response = await PostBytesAsync(PackInstallRoutes.InstallRoute,
            await ExportAsync(TemplatePackBody(content)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(_store.ListInstalled(NodeTenantFor()), pack => pack.PackKey == "acme.contract");
    }

    [Fact(DisplayName = "T-1033: interim, a valid flat template without a declaration still installs until T-1026")]
    public async Task Interim_install_admits_a_flat_template_with_no_declared_contract()
    {
        var content = TemplateContent(contract: null);
        Assert.True(PackTemplateContent.TryParse(content, out var template, out var error), error);
        Assert.Null(template.Envelope.Contract);

        using var response = await PostBytesAsync(PackInstallRoutes.InstallRoute,
            await ExportAsync(TemplatePackBody(content)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(_store.ListInstalled(NodeTenantFor()), pack => pack.PackKey == "acme.contract");
    }

    [Theory(DisplayName = "T-1033: each native definition's declared newer major or minor is refused before installation")]
    [InlineData(PackContentKind.FormDefinition, 2, 0, "acme.contract.form@1.0.0")]
    [InlineData(PackContentKind.FormDefinition, 1, 1, "acme.contract.form@1.0.0")]
    [InlineData(PackContentKind.TaxonomyDefinition, 2, 0, "acme.contract.taxonomy@1.0.0")]
    [InlineData(PackContentKind.TaxonomyDefinition, 1, 1, "acme.contract.taxonomy@1.0.0")]
    [InlineData(PackContentKind.ReportDefinition, 2, 0, "acme.contract.report@1.0.0")]
    [InlineData(PackContentKind.ReportDefinition, 1, 1, "acme.contract.report@1.0.0")]
    [InlineData(PackContentKind.ViewDefinition, 2, 0, "acme.contract.view@1.0.0")]
    [InlineData(PackContentKind.ViewDefinition, 1, 1, "acme.contract.view@1.0.0")]
    [InlineData(PackContentKind.ScheduleDefinition, 2, 0, "acme.contract.schedule@1.0.0")]
    [InlineData(PackContentKind.ScheduleDefinition, 1, 1, "acme.contract.schedule@1.0.0")]
    [InlineData(PackContentKind.DataExchangeDefinition, 2, 0, "acme.contract.exchange@1.0.0")]
    [InlineData(PackContentKind.DataExchangeDefinition, 1, 1, "acme.contract.exchange@1.0.0")]
    public async Task Install_refuses_each_native_definition_outside_the_literal_window(
        PackContentKind kind, int major, int minor, string expectedTarget)
    {
        var declared = new DefinitionContractVersion(major, minor);
        var content = NativeContent(kind, declared);
        AssertNativeMetadata(kind, content, declared);

        using var response = await PostBytesAsync(PackInstallRoutes.InstallRoute,
            await ExportAsync(NativePackBody(kind, content)));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("installed").GetBoolean());
        Assert.Equal("install", body.GetProperty("stage").GetString());
        Assert.Equal(new[] { "definition.contract.out_of_window" },
            body.GetProperty("refusalCodes").EnumerateArray().Select(code => code.GetString()));
        var refusal = Assert.Single(body.GetProperty("refusals").EnumerateArray());
        Assert.Equal("definition.contract.out_of_window", refusal.GetProperty("code").GetString());
        Assert.Equal("/envelope/contract", refusal.GetProperty("pointer").GetString());
        Assert.Equal(expectedTarget, refusal.GetProperty("target").GetString());
        var window = body.GetProperty("contract");
        Assert.Equal(1, window.GetProperty("app_major").GetInt32());
        Assert.Equal(0, window.GetProperty("app_minor").GetInt32());
        Assert.Equal(new[] { 1, 1 }, window.GetProperty("window").EnumerateArray().Select(edge => edge.GetInt32()));
        Assert.Empty(_store.ListInstalled(NodeTenantFor()));
    }

    [Theory(DisplayName = "T-1033: canonical native definitions install and project with literal 1.0 or the interim omitted declaration")]
    [InlineData(PackContentKind.FormDefinition, true)]
    [InlineData(PackContentKind.FormDefinition, false)]
    [InlineData(PackContentKind.TaxonomyDefinition, true)]
    [InlineData(PackContentKind.TaxonomyDefinition, false)]
    [InlineData(PackContentKind.ReportDefinition, true)]
    [InlineData(PackContentKind.ReportDefinition, false)]
    [InlineData(PackContentKind.ViewDefinition, true)]
    [InlineData(PackContentKind.ViewDefinition, false)]
    [InlineData(PackContentKind.ScheduleDefinition, true)]
    [InlineData(PackContentKind.ScheduleDefinition, false)]
    [InlineData(PackContentKind.DataExchangeDefinition, true)]
    [InlineData(PackContentKind.DataExchangeDefinition, false)]
    public async Task Install_and_activate_valid_native_definitions(PackContentKind kind, bool declaresContract)
    {
        var declared = declaresContract ? new DefinitionContractVersion(1, 0) : null;
        var content = NativeContent(kind, declared);
        AssertNativeMetadata(kind, content, declared);

        using var install = await PostBytesAsync(PackInstallRoutes.InstallRoute,
            await ExportAsync(NativePackBody(kind, content)));
        Assert.Equal(HttpStatusCode.OK, install.StatusCode);
        var installed = await install.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(installed.GetProperty("installed").GetBoolean());
        Assert.Equal("acme.contract", Assert.Single(_store.ListInstalled(NodeTenantFor())).PackKey);

        // Install stores a draft; activation must also admit the real body through the host descriptors.
        PlatformPackTestPreload.Activate(_store, NodeTenantFor());
        using var activate = await _client.PostAsJsonAsync(PackInstallRoutes.ActivateRoute,
            new { packKey = "acme.contract", version = "1.0.0" });
        Assert.Equal(HttpStatusCode.OK, activate.StatusCode);
        var activated = await activate.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(activated.GetProperty("activated").GetBoolean());
        Assert.Empty(activated.GetProperty("projectionRefusals").EnumerateArray());
        await AssertNativeProjectedAsync(kind);
    }

    [Fact(DisplayName = "T-1033: every current pack kind has an explicit wire classification and declared fixtures use the real check")]
    public void New_pack_kinds_require_an_explicit_wire_fixture_or_documented_boundary()
    {
        // This is a wire inventory, independent of the check's supported-kind selection.
        var fixtures = new Dictionary<PackContentKind, (string Shape, string? Key, JsonObject? Body)>
        {
            [PackContentKind.FormDefinition] = ("definitionEnvelope.contract", "acme.contract.form", NativeContent(PackContentKind.FormDefinition, new(2, 0))),
            [PackContentKind.WorkflowDefinition] = ("authored workflow; lean mapper omits contract", null, null),
            [PackContentKind.StandardsCatalog] = ("standards catalogue wire; outside declared-definition fixtures", null, null),
            [PackContentKind.NavWorkspaceConfig] = ("navigation workspace wire; outside declared-definition fixtures", null, null),
            [PackContentKind.CascadeDefaults] = ("cascade defaults wire; outside declared-definition fixtures", null, null),
            [PackContentKind.AssetTypeDefinition] = ("asset-type descriptor wire; outside declared-definition fixtures", null, null),
            [PackContentKind.TerminologyOverride] = ("terminology override wire; outside declared-definition fixtures", null, null),
            [PackContentKind.TemplateDefinition] = ("contract", "acme.invoice", TemplateContent(new JsonObject { ["major"] = 2, ["minor"] = 0 })),
            [PackContentKind.TaxonomyDefinition] = ("envelope.contract", "acme.contract.taxonomy", NativeContent(PackContentKind.TaxonomyDefinition, new(2, 0))),
            [PackContentKind.ReportDefinition] = ("envelope.contract", "acme.contract.report", NativeContent(PackContentKind.ReportDefinition, new(2, 0))),
            [PackContentKind.ViewDefinition] = ("envelope.contract", "acme.contract.view", NativeContent(PackContentKind.ViewDefinition, new(2, 0))),
            [PackContentKind.ScheduleDefinition] = ("envelope.contract", "acme.contract.schedule", NativeContent(PackContentKind.ScheduleDefinition, new(2, 0))),
            [PackContentKind.DataExchangeDefinition] = ("envelope.contract", "acme.contract.exchange", NativeContent(PackContentKind.DataExchangeDefinition, new(2, 0))),
            [PackContentKind.StandingRuleDefinition] = ("standing-rule handler wire; outside declared-definition fixtures", null, null),
            [PackContentKind.RoleDefinition] = ("role declaration wire; outside declared-definition fixtures", null, null),
            [PackContentKind.AuthorizationCapabilityBinding] = ("authorization binding wire; outside declared-definition fixtures", null, null),
            [PackContentKind.RecordType] = ("record-type descriptor wire; outside declared-definition fixtures", null, null),
            [PackContentKind.Layout] = ("envelope.contract", "acme.surface", PlatformWindowContent()),
            [PackContentKind.Resource] = ("envelope.contract", "acme.surface", PlatformWindowContent()),
            [PackContentKind.Bookable] = ("envelope.contract", "acme.surface", PlatformWindowContent()),
        };
        Assert.Equal(Enum.GetValues<PackContentKind>().OrderBy(kind => kind), fixtures.Keys.OrderBy(kind => kind));
        foreach (var registered in PackSeedProjector.RegisteredCases)
            Assert.True(fixtures.ContainsKey(registered.ContentKind), $"Unclassified projection kind: {registered.ContentKind}");

        foreach (var (kind, fixture) in fixtures)
        {
            Assert.False(string.IsNullOrWhiteSpace(fixture.Shape));
            if (fixture.Body is null) continue;
            JsonNode? declaration = fixture.Body;
            foreach (var segment in fixture.Shape.Split('.')) declaration = declaration![segment];
            Assert.Equal(2, declaration!["major"]!.GetValue<int>());
            Assert.Equal(0, declaration["minor"]!.GetValue<int>());
            var item = new PackContentCanonicalizer().Canonicalize(
                new PackContentSource(fixture.Key!, kind, "1.0.0", fixture.Body));
            var refusal = Assert.Single(PackContractWindowCheck.FindRefusals([item], new DefinitionContractWindow(1, 0, 1)));
            Assert.Equal("definition.contract.out_of_window", refusal.Code);
            Assert.Equal("/envelope/contract", refusal.Pointer);
            Assert.Equal(fixture.Key + "@1.0.0", refusal.Target);
        }
    }

    [Fact(DisplayName = "T-1033: the existing workflow wire mapper omits declared contract metadata")]
    public void Workflow_contract_mapping_is_an_explicit_remaining_boundary()
    {
        var authored = JsonSerializer.SerializeToElement(new
        {
            key = "acme.contract.workflow", version = "1.0.0", tenant = NodeTenantFor().Value,
            envelope = new { contract = new { major = 2, minor = 0 } },
            initialState = "Done", states = new[] { new { id = "Done", kind = "Terminal" } },
            transitions = Array.Empty<object>(), triggers = Array.Empty<object>(), actions = Array.Empty<object>(),
        });
        var wire = WorkflowDefinitionWireMapper.CanonicalizeAuthoredWire(authored);
        Assert.Equal(2, wire["envelope"]!["contract"]!["major"]!.GetValue<int>());
        Assert.Equal(0, wire["envelope"]!["contract"]!["minor"]!.GetValue<int>());
        var model = WorkflowDefinitionWireMapper.ToModel(authored);
        Assert.Null(model.Envelope.Contract);
        var item = new PackContentCanonicalizer().Canonicalize(
            new PackContentSource("acme.contract.workflow", PackContentKind.WorkflowDefinition, "1.0.0", wire));
        Assert.Empty(PackContractWindowCheck.FindRefusals([item], new DefinitionContractWindow(1, 0, 1)));
    }

    [Theory(DisplayName = "T-1033: unreadable native declarations retain the interim no-window-refusal behavior")]
    [InlineData(PackContentKind.FormDefinition, "definitionEnvelope")]
    [InlineData(PackContentKind.TaxonomyDefinition, "envelope")]
    [InlineData(PackContentKind.ReportDefinition, "envelope")]
    [InlineData(PackContentKind.ViewDefinition, "envelope")]
    [InlineData(PackContentKind.ScheduleDefinition, "envelope")]
    [InlineData(PackContentKind.DataExchangeDefinition, "envelope")]
    public void Unreadable_native_declarations_are_not_missing_declaration_refusals(PackContentKind kind, string property)
    {
        var content = NativeContent(kind, new DefinitionContractVersion(1, 0));
        content[property]!["contract"] = new JsonObject { ["major"] = "2", ["minor"] = 0 };
        var item = new PackContentCanonicalizer().Canonicalize(new PackContentSource(NativeKey(kind), kind, "1.0.0", content));
        Assert.Empty(PackContractWindowCheck.FindRefusals([item], new DefinitionContractWindow(1, 0, 1)));
    }

    private static JsonObject PlatformWindowContent() => new()
    {
        ["envelope"] = new JsonObject { ["contract"] = new JsonObject { ["major"] = 2, ["minor"] = 0 } },
    };

    private static string NativeKey(PackContentKind kind) => kind switch
    {
        PackContentKind.FormDefinition => "acme.contract.form",
        PackContentKind.TaxonomyDefinition => "acme.contract.taxonomy",
        PackContentKind.ReportDefinition => "acme.contract.report",
        PackContentKind.ViewDefinition => "acme.contract.view",
        PackContentKind.ScheduleDefinition => "acme.contract.schedule",
        PackContentKind.DataExchangeDefinition => "acme.contract.exchange",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static DefinitionEnvelope<string, string, string, JsonElement> NativeEnvelope(
        string key, DefinitionContractVersion? contract) => new(
            key, "1.0.0", NodeTenantFor().Value, CascadeLayer.Tenant,
            JsonSerializer.SerializeToElement(new { }), Array.Empty<DefinitionRequirement>(), contract);

    private static JsonObject NativeContent(PackContentKind kind, DefinitionContractVersion? contract)
    {
        var key = NativeKey(kind);
        object definition = kind switch
        {
            PackContentKind.TaxonomyDefinition => new TaxonomyDefinition
            {
                Envelope = new DefinitionEnvelope<TaxonomyDefinitionId, TaxonomyVersion, ActorId, TaxonomyLineage?>(
                    new("acme", "contract", "taxonomy"), new(1, 0, 0), new ActorId("contract-author"),
                    CascadeLayer.Tenant, null, Array.Empty<DefinitionRequirement>(), contract),
                Id = new("acme", "contract", "taxonomy"), Version = new(1, 0, 0),
                Governance = TaxonomyGovernanceRegime.Civilian, Description = "Contract taxonomy",
                Owner = new ActorId("contract-author"), PublishedAt = new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero),
            },
            PackContentKind.ReportDefinition => new ReportDefinition
            {
                Envelope = NativeEnvelope(key, contract), Key = key, Version = "1.0.0", Tenant = NodeTenantFor().Value,
                SchemaVersion = 1, ReportKind = "trial-balance", Title = "Contract report",
                Parameters = JsonSerializer.SerializeToElement(new { chart = "contract-chart" }),
            },
            PackContentKind.ViewDefinition => new ViewDefinition
            {
                Envelope = NativeEnvelope(key, contract), Key = key, Version = "1.0.0", Tenant = NodeTenantFor().Value,
                SchemaVersion = 1, ViewKind = Harborline.Blocks.EntityViews.ViewKindIds.Table, Title = "Contract view",
                Parameters = JsonSerializer.SerializeToElement(new { entityType = "AccessGrant" }),
            },
            PackContentKind.ScheduleDefinition => new ScheduleDefinition
            {
                Envelope = NativeEnvelope(key, contract), Key = key, Version = "1.0.0", Tenant = NodeTenantFor().Value,
                SchemaVersion = 1, ScheduleKind = "harborline.scheduling-definition-draft/v0", Title = "Contract schedule",
                Body = JsonSerializer.SerializeToElement(new
                {
                    schema = "harborline.scheduling-definition-draft/v0", title = "Contract schedule", timezone = "UTC",
                    activities = new[] { new { id = "visit", durationMinutes = 30 } }, resourceRequirements = Array.Empty<string>(),
                }),
            },
            PackContentKind.DataExchangeDefinition => new DataExchangeDefinition
            {
                Envelope = NativeEnvelope(key, contract), Key = key, Version = "1.0.0", Tenant = NodeTenantFor().Value,
                SchemaVersion = 1, ExchangeKind = "import.erpnext/maria-db-dump", Title = "Contract exchange",
                Settings = JsonSerializer.SerializeToElement(new { mapping = "contract" }),
            },
            PackContentKind.FormDefinition => PackProjectionTestFixture.FormContent("Contract form"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var content = JsonSerializer.SerializeToNode(definition, Json)!.AsObject();
        if (kind == PackContentKind.FormDefinition)
        {
            content["definitionEnvelope"] = JsonSerializer.SerializeToNode(
                new DefinitionEnvelope<FormDefinitionId, SemanticVersion, TenantId, FormDefinitionProvenance>(
                    new FormDefinitionId(key), SemanticVersion.Parse("1.0.0"), new TenantId(NodeTenantFor().Value),
                    CascadeLayer.Pack, new FormDefinitionProvenance(IdentityRef.System, null),
                    Array.Empty<DefinitionRequirement>(), contract), Json);
        }
        if (contract is null)
            content[kind == PackContentKind.FormDefinition ? "definitionEnvelope" : "envelope"]!.AsObject().Remove("contract");
        return content;
    }

    private static void AssertNativeMetadata(PackContentKind kind, JsonObject content, DefinitionContractVersion? expected)
    {
        DefinitionContractVersion? actual;
        if (kind == PackContentKind.FormDefinition)
        {
            Assert.True(PackFormDefinitionContent.TryParse(content, out _, out var envelope, out var error), error);
            Assert.NotNull(envelope);
            actual = envelope.Contract;
        }
        else actual = kind switch
        {
            PackContentKind.TaxonomyDefinition => content.Deserialize<TaxonomyDefinition>(Json)!.Envelope.Contract,
            PackContentKind.ReportDefinition => content.Deserialize<ReportDefinition>(Json)!.Envelope.Contract,
            PackContentKind.ViewDefinition => content.Deserialize<ViewDefinition>(Json)!.Envelope.Contract,
            PackContentKind.ScheduleDefinition => content.Deserialize<ScheduleDefinition>(Json)!.Envelope.Contract,
            PackContentKind.DataExchangeDefinition => content.Deserialize<DataExchangeDefinition>(Json)!.Envelope.Contract,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        Assert.Equal(expected, actual);
        if (expected is null)
            Assert.False(content[kind == PackContentKind.FormDefinition ? "definitionEnvelope" : "envelope"]!.AsObject().ContainsKey("contract"));
    }

    private async Task AssertNativeProjectedAsync(PackContentKind kind)
    {
        switch (kind)
        {
            case PackContentKind.FormDefinition:
                var form = await _forms.GetAsync(new DefinitionCoordinates(new TenantId(NodeTenantFor().Value), "acme.contract.form", "1.0.0"));
                Assert.Equal("acme.contract.form", form.Id.Value);
                Assert.Equal("1.0.0", form.Version.ToString());
                Assert.Equal(FormDefinitionStatus.Published, form.Status);
                break;
            case PackContentKind.TaxonomyDefinition:
                var taxonomy = await _taxonomies.GetDefinitionAsync(new TenantId(NodeTenantFor().Value),
                    new TaxonomyDefinitionId("acme", "contract", "taxonomy"), new TaxonomyVersion(1, 0, 0), CancellationToken.None);
                Assert.NotNull(taxonomy);
                Assert.Equal("acme.contract.taxonomy", taxonomy.Id.Value);
                Assert.Equal("Contract taxonomy", taxonomy.Description);
                break;
            case PackContentKind.ReportDefinition:
                var report = await _reports.GetDefinitionAsync(NodeTenantFor().Value, "acme.contract.report", "1.0.0");
                Assert.NotNull(report);
                Assert.Equal("trial-balance", report.ReportKind);
                Assert.Equal("Contract report", report.Title);
                break;
            case PackContentKind.ViewDefinition:
                var view = await _views.GetDefinitionAsync(NodeTenantFor().Value, "acme.contract.view", "1.0.0");
                Assert.NotNull(view);
                Assert.Equal("AccessGrant", view.Parameters.GetProperty("entityType").GetString());
                Assert.Equal("Contract view", view.Title);
                break;
            case PackContentKind.ScheduleDefinition:
                var schedule = await _schedules.GetDefinitionAsync(NodeTenantFor().Value, "acme.contract.schedule", "1.0.0");
                Assert.NotNull(schedule);
                Assert.Equal("harborline.scheduling-definition-draft/v0", schedule.ScheduleKind);
                Assert.Equal(30, Assert.Single(schedule.Body.GetProperty("activities").EnumerateArray()).GetProperty("durationMinutes").GetInt32());
                break;
            case PackContentKind.DataExchangeDefinition:
                var exchange = await _exchanges.GetDefinitionAsync(NodeTenantFor().Value, "acme.contract.exchange", "1.0.0");
                Assert.NotNull(exchange);
                Assert.Equal("import.erpnext/maria-db-dump", exchange.ExchangeKind);
                Assert.Equal("contract", exchange.Settings.GetProperty("mapping").GetString());
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    private static object NativePackBody(PackContentKind kind, JsonObject content) => new
    {
        key = "acme.contract", version = "1.0.0", name = "Acme Contract", description = "contract window pack", scopeTier = "Vertical",
        contents = new[] { new { key = NativeKey(kind), kind = kind.ToString(), version = "1.0.0", content } },
    };

    private static JsonObject TemplateContent(JsonObject? contract)
    {
        // The actual flat pack wire, not a serialized internal TemplateDefinition envelope.
        var content = JsonNode.Parse("""
            {
              "key": "acme.invoice",
              "version": "1.0.0",
              "documentType": "invoice",
              "recordType": { "type": "invoice", "version": "1" },
              "locale": { "kind": "fixed", "tag": "en-US" },
              "structure": [ { "kind": "header", "lines": [ { "runs": [ { "literal": "Invoice" } ] } ] } ]
            }
            """)!.AsObject();
        if (contract is not null) content["contract"] = contract;
        return content;
    }

    private static object TemplatePackBody(JsonObject content) => new
    {
        key = "acme.contract",
        version = "1.0.0",
        name = "Acme Contract",
        description = "contract window pack",
        scopeTier = "Vertical",
        contents = new[]
        {
            new { key = "acme.invoice", kind = "TemplateDefinition", version = "1.0.0", content },
        },
    };

    private static void AssertReportsTheSeedWindow(JsonElement body)
    {
        var window = PlatformPackageSeed.ContractWindow;
        var contract = body.GetProperty("contract");
        Assert.Equal(window.Major, contract.GetProperty("app_major").GetInt32());
        Assert.Equal(window.Minor, contract.GetProperty("app_minor").GetInt32());
        Assert.Equal([window.OldestMajor, window.Major],
            contract.GetProperty("window").EnumerateArray().Select(edge => edge.GetInt32()));
    }

    private async Task<byte[]> ExportAsync(object body)
    {
        using var response = await _client.PostAsJsonAsync(PackComposerRoutes.ExportRoute, body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsByteArrayAsync();
    }

    private Task<HttpResponseMessage> PostBytesAsync(string route, byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return _client.PostAsync(route, content);
    }

    private static object PackBody(string kind, object? contract, string key = "acme.contract") => new
    {
        key,
        version = "1.0.0",
        name = "Acme Contract",
        description = "contract window pack",
        scopeTier = "Vertical",
        contents = new[]
        {
            new
            {
                key = "acme.surface",
                kind,
                version = "1.0.0",
                content = (object)new { envelope = new { contract } },
            },
        },
    };

    private static Harborline.Foundation.Assets.Common.TenantId NodeTenantFor()
        => Harborline.Api.LocalNodeHost.Data.Financial.NodeTenant.Resolve(new MutableActiveTeamAccessor(new TeamContext(
            TeamA, "Team A", new ServiceCollection().BuildServiceProvider(), TimeProvider.System)));

    private sealed class MutableActiveTeamAccessor(TeamContext? active) : IActiveTeamAccessor
    {
        public TeamContext? Active { get; set; } = active;
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged;
        private void Keep() => ActiveChanged?.Invoke(this, new ActiveTeamChangedEventArgs(null, null));
    }
}
