using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
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
using Harborline.Api.Foundation.Blobs;
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
            Harborline.Api.LocalNodeHost.Tests.Authorization.TestAuthorization.AllowGate(),
            layoutCapabilities: new Dictionary<string, string> { ["platform.layout"] = "1.0.0" },
            layoutRegisters: new LayoutHostRegisters(LayoutBlockKindRegistry.Platform,
                FieldControls: LayoutFieldControlRegistry.Released, ValidationRules: LayoutValidationRuleRegistry.Released));
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
        if (kind != "Layout") SeedBookingType();

        using var response = await PostBytesAsync(PackInstallRoutes.InstallRoute,
            await ExportAsync(PackBody(kind, new { major = 1, minor = 0 })));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(_store.ListInstalled(NodeTenantFor()), pack => pack.PackKey == "acme.contract");
    }

    [Theory(DisplayName = "T-1034 kernel-core-ck-8: placeholder Booking definitions are refused")]
    [InlineData("Resource")]
    [InlineData("Bookable")]
    public async Task Placeholder_booking_definition_is_refused(string kind)
    {
        using var response = await PostBytesAsync(PackInstallRoutes.InstallRoute,
            await ExportAsync(new
            {
                key = "acme.contract", version = "1.0.0", name = "Acme Contract",
                description = "contract window pack", scopeTier = "Vertical",
                contents = new[] { new { key = "acme.surface", kind, version = "1.0.0",
                    content = new { envelope = new { identity = "acme.surface", version = "1.0.0",
                        tenant = NodeTenantFor().Value, contract = new { major = 1, minor = 0 } } } } },
            }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("installed").GetBoolean());
        Assert.Equal("install", body.GetProperty("stage").GetString());
        var refusal = Assert.Single(body.GetProperty("refusals").EnumerateArray());
        Assert.Equal("booking.definition.kind_mismatch", refusal.GetProperty("code").GetString());
        Assert.Equal("/kind", refusal.GetProperty("pointer").GetString());
        Assert.Equal("acme.surface@1.0.0", refusal.GetProperty("target").GetString());
        Assert.Empty(_store.ListInstalled(NodeTenantFor()));
    }

    [Theory(DisplayName = "T-1034 booking-auth-20: an unknown record type is refused")]
    [InlineData("Resource", "booking.definition.type_unknown", "/from_type_id")]
    [InlineData("Bookable", "booking.definition.type_unknown", "/on_type_id")]
    public async Task Booking_unknown_record_type_is_refused(string kind, string code, string pointer)
    {
        var content = BookingContent(kind, "acme.surface");
        if (kind == "Bookable") content["requires"] = new JsonArray();
        using var response = await PostBytesAsync(PackInstallRoutes.InstallRoute,
            await ExportAsync(new { key = "acme.contract", version = "1.0.0", name = "Acme Contract",
                description = "contract window pack", scopeTier = "Vertical",
                contents = new[] { new { key = "acme.surface", kind, version = "1.0.0", content } } }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("installed").GetBoolean());
        Assert.Equal("install", body.GetProperty("stage").GetString());
        var refusal = Assert.Single(body.GetProperty("refusals").EnumerateArray());
        Assert.Equal(code, refusal.GetProperty("code").GetString());
        Assert.Equal(pointer, refusal.GetProperty("pointer").GetString());
        Assert.Equal("acme.surface@1.0.0", refusal.GetProperty("target").GetString());
        Assert.Empty(_store.ListInstalled(NodeTenantFor()));
    }

    [Fact(DisplayName = "T-1034 booking-auth-20: Preview, Check and Install share Booking admission")]
    public async Task All_plan_paths_refuse_unknown_booking_type()
    {
        using (var seed = await PostBytesAsync(PackInstallRoutes.InstallRoute,
            await ExportAsync(PackBody("Layout", new { major = 1, minor = 0 }, key: "acme.seed"))))
            Assert.Equal(HttpStatusCode.OK, seed.StatusCode);
        var content = BookingContent("Bookable", "acme.surface");
        var pack = new { key = "acme.contract", version = "1.0.0", name = "Acme Contract", description = "contract window pack",
            scopeTier = "Vertical", contents = new[] { new { key = "acme.surface", kind = "Bookable", version = "1.0.0", content } } };
        var bytes = await ExportAsync(pack);
        var before = JsonSerializer.Serialize(_store.ListInstalled(NodeTenantFor()));
        foreach (var route in new[] { PackInstallRoutes.PreviewRoute, PackInstallRoutes.CheckRoute, PackInstallRoutes.InstallRoute })
        {
            using var response = await PostBytesAsync(route, bytes);
            Assert.Equal(route == PackInstallRoutes.InstallRoute ? HttpStatusCode.UnprocessableEntity : HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            var refusal = Assert.Single(body.GetProperty("refusals").EnumerateArray());
            Assert.Equal("booking.definition.type_unknown", refusal.GetProperty("code").GetString());
            Assert.Equal("/on_type_id", refusal.GetProperty("pointer").GetString());
            Assert.Equal("acme.surface@1.0.0", refusal.GetProperty("target").GetString());
            Assert.Equal(before, JsonSerializer.Serialize(_store.ListInstalled(NodeTenantFor())));
        }
    }

    [Theory(DisplayName = "T-1034 DES-0025 section 7: undecodable Booking bodies refuse before state changes")]
    [InlineData("null")]
    [InlineData("{")]
    public async Task Undecodable_booking_body_is_refused(string raw)
    {
        SeedBookingType();
        var bytes = await ExportAsync(PackBody("Resource", new { major = 1, minor = 0 }));
        var codec = new PackFileCodec();
        var file = Assert.IsType<PackFile>(codec.TryDecode(bytes));
        var envelope = Assert.IsType<SignedOperation<PackSignatureSubject>>(file.Envelope);
        var rawBytes = Encoding.UTF8.GetBytes(raw);
        var contents = file.Contents.Select(item => item.Key == "acme.surface"
            ? item with { ContentBase64 = Convert.ToBase64String(rawBytes) } : item).ToList();
        var manifest = envelope.Payload.Manifest with { Contents = envelope.Payload.Manifest.Contents.Select(item =>
            item.Key == "acme.surface" ? item with { ContentAddress = Cid.FromBytes(rawBytes) } : item).ToList() };
        var signature = await new Ed25519Signer(_key).SignAsync(envelope.Payload with { Manifest = manifest },
            envelope.IssuedAt, Guid.NewGuid());
        bytes = codec.Encode(file with { Envelope = signature, Contents = contents });
        var before = JsonSerializer.Serialize(_store.ListInstalled(NodeTenantFor()));
        foreach (var route in new[] { PackInstallRoutes.PreviewRoute, PackInstallRoutes.CheckRoute, PackInstallRoutes.InstallRoute })
        {
            using var response = await PostBytesAsync(route, bytes);
            Assert.Equal(route == PackInstallRoutes.InstallRoute ? HttpStatusCode.UnprocessableEntity : HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            var refusal = Assert.Single(body.GetProperty("refusals").EnumerateArray());
            Assert.Equal("booking.definition.body_invalid", refusal.GetProperty("code").GetString());
            Assert.Equal("", refusal.GetProperty("pointer").GetString());
            Assert.Equal("acme.surface@1.0.0", refusal.GetProperty("target").GetString());
            Assert.Equal(before, JsonSerializer.Serialize(_store.ListInstalled(NodeTenantFor())));
        }
    }

    [Theory(DisplayName = "T-1034 booking-ck-3, booking-auth-11, booking-ck-17: platform Booking refusals retain their definition pointers")]
    [InlineData("trait", "booking.resource.type_not_admitted", "/from_type_id")]
    [InlineData("resource", "booking.bookable.required_resource_unknown", "/requires/0")]
    [InlineData("identity", "booking.envelope.mismatch", "/envelope/identity")]
    public async Task Booking_refusals_name_the_definition(string failure, string code, string pointer)
    {
        var kind = failure == "trait" ? "Resource" : "Bookable";
        var content = BookingContent(kind, "acme.surface");
        content[kind == "Resource" ? "from_type_id" : "on_type_id"] = "acme.room";
        if (failure == "resource") content["requires"] = new JsonArray("acme.missing");
        if (failure == "identity") content["envelope"]!["identity"] = "acme.other";
        var record = BookingRecordType(false);
        var pack = new { key = "acme.contract", version = "1.0.0", name = "Acme Contract",
            description = "contract window pack", scopeTier = "Vertical", contents = new object[]
            {
                new { key = "acme.room", kind = "AssetTypeDefinition", version = "1.0.0", content = record },
                new { key = "acme.surface", kind, version = "1.0.0", content },
            } };
        using var response = await PostBytesAsync(PackInstallRoutes.InstallRoute, await ExportAsync(pack));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("installed").GetBoolean());
        Assert.Equal("install", body.GetProperty("stage").GetString());
        var refusal = Assert.Single(body.GetProperty("refusals").EnumerateArray());
        Assert.Equal(code, refusal.GetProperty("code").GetString());
        Assert.Equal(pointer, refusal.GetProperty("pointer").GetString());
        Assert.Equal("acme.surface@1.0.0", refusal.GetProperty("target").GetString());
        Assert.Empty(_store.ListInstalled(NodeTenantFor()));
    }

    internal static JsonObject BookingRecordType(bool resourceTrait, string package = "acme.contract")
    {
        var record = JsonNode.Parse("""
            {"class_id":"acme.assets","envelope":{"contract":{"major":1,"minor":0},"package_id":"acme.contract","section":"acme","tenant":"tenant-a"},"fields":[],"name":"Room","record_class":"master","record_type_id":"acme.room"}
            """)!.AsObject();
        record["envelope"]!["package_id"] = package;
        if (resourceTrait)
            record["traits"] = JsonNode.Parse("""[{"trait_id":"platform.trait.bookable-resource","version":"1.0.0","slot_bindings":[]}]""");
        return record;
    }

    [Theory(DisplayName = "T-1034 booking-auth-20: only declared dependencies supply Booking types and Resources")]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Booking_dependency_is_resolved_only_when_declared(bool declared, bool succeeds)
    {
        var record = BookingRecordType(true, "acme.producer").ToJsonString();
        var resource = BookingContent("Resource", "acme.room-resource");
        resource["from_type_id"] = "acme.room";
        PackSeedItem Seed(string key, PackContentKind kind, string body) =>
            new(key, kind, "1.0.0", body, Cid.FromBytes(Encoding.UTF8.GetBytes(body)));
        var producer = RecordsInstallClosureGateTests.Producer("acme.producer", "1.0.0",
            [Seed("acme.type", PackContentKind.AssetTypeDefinition, record),
             Seed("acme.room-resource", PackContentKind.Resource, resource.ToJsonString())],
            ["acme.type"], [new PackExposedDefinition("acme.type", "1.0.0", "sha256:" + new string('a', 64))]);
        _store.Commit(new PackInstallTransaction(NodeTenantFor(), producer,
            new PackInstallWatermark("acme.producer", "1.0.0", new Dictionary<string, int>()), []));
        var content = BookingContent("Bookable", "acme.surface");
        content["on_type_id"] = "acme.room";
        content["requires"] = new JsonArray("acme.room-resource");
        var pack = new { key = "acme.contract", version = "1.0.0", name = "Acme Contract",
            description = "contract window pack", scopeTier = "Vertical",
            dependencies = declared ? new[] { new { key = "acme.producer", version = "1.0.0" } } : [],
            contents = new[] { new { key = "acme.surface", kind = "Bookable", version = "1.0.0", content } } };
        using var response = await PostBytesAsync(PackInstallRoutes.InstallRoute, await ExportAsync(pack));
        Assert.Equal(succeeds ? HttpStatusCode.OK : HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(succeeds, body.GetProperty("installed").GetBoolean());
        if (!succeeds)
        {
            Assert.Equal("install", body.GetProperty("stage").GetString());
            var refusals = body.GetProperty("refusals").EnumerateArray().ToArray();
            Assert.Equal(2, refusals.Length);
            Assert.Equal("booking.definition.type_unknown", refusals[0].GetProperty("code").GetString());
            Assert.Equal("/on_type_id", refusals[0].GetProperty("pointer").GetString());
            Assert.Equal("acme.surface@1.0.0", refusals[0].GetProperty("target").GetString());
            Assert.Equal("booking.bookable.required_resource_unknown", refusals[1].GetProperty("code").GetString());
            Assert.Equal("/requires/0", refusals[1].GetProperty("pointer").GetString());
            Assert.Equal("acme.surface@1.0.0", refusals[1].GetProperty("target").GetString());
            Assert.Single(_store.ListInstalled(NodeTenantFor()));
        }
    }

    [Fact(DisplayName = "T-1034 booking-auth-11: legacy dependency Resource must be admitted")]
    public async Task Legacy_placeholder_dependency_resource_is_not_admitted()
    {
        var record = BookingRecordType(true, "acme.producer").ToJsonString();
        PackSeedItem Seed(string key, PackContentKind kind, string body) =>
            new(key, kind, "1.0.0", body, Cid.FromBytes(Encoding.UTF8.GetBytes(body)));
        var producer = RecordsInstallClosureGateTests.Producer("acme.producer", "1.0.0",
            [Seed("acme.type", PackContentKind.AssetTypeDefinition, record),
             Seed("acme.room-resource", PackContentKind.Resource, "{}")],
            ["acme.type"], [new PackExposedDefinition("acme.type", "1.0.0", "sha256:" + new string('a', 64))]);
        _store.Commit(new PackInstallTransaction(NodeTenantFor(), producer,
            new PackInstallWatermark("acme.producer", "1.0.0", new Dictionary<string, int>()), []));
        var before = JsonSerializer.Serialize(_store.ListInstalled(NodeTenantFor()));
        var content = BookingContent("Bookable", "acme.surface");
        content["on_type_id"] = "acme.room";
        content["requires"] = new JsonArray("acme.room-resource");
        var pack = new { key = "acme.contract", version = "1.0.0", name = "Acme Contract",
            description = "contract window pack", scopeTier = "Vertical",
            dependencies = new[] { new { key = "acme.producer", version = "1.0.0" } },
            contents = new[] { new { key = "acme.surface", kind = "Bookable", version = "1.0.0", content } } };
        var bytes = await ExportAsync(pack);
        foreach (var route in new[] { PackInstallRoutes.PreviewRoute, PackInstallRoutes.CheckRoute, PackInstallRoutes.InstallRoute })
        {
            using var response = await PostBytesAsync(route, bytes);
            Assert.Equal(route == PackInstallRoutes.InstallRoute ? HttpStatusCode.UnprocessableEntity : HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            var refusal = Assert.Single(body.GetProperty("refusals").EnumerateArray());
            Assert.Equal("booking.bookable.required_resource_unknown", refusal.GetProperty("code").GetString());
            Assert.Equal("/requires/0", refusal.GetProperty("pointer").GetString());
            Assert.Equal("acme.surface@1.0.0", refusal.GetProperty("target").GetString());
            Assert.Equal(before, JsonSerializer.Serialize(_store.ListInstalled(NodeTenantFor())));
        }
    }

    [Theory(DisplayName = "T-1034 DES-0007 s7 collisions have no guessed winner")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Duplicate_record_type_ids_are_unknown_in_either_dependency_order(bool reverse)
    {
        foreach (var key in new[] { "acme.first", "acme.second" })
        {
            var record = BookingRecordType(true, key).ToJsonString();
            var seed = new PackSeedItem(key + ".type", PackContentKind.AssetTypeDefinition, "1.0.0", record,
                Cid.FromBytes(Encoding.UTF8.GetBytes(record)));
            var producer = RecordsInstallClosureGateTests.Producer(key, "1.0.0", [seed], [seed.Key],
                [new PackExposedDefinition(seed.Key, "1.0.0", "sha256:" + new string('a', 64))]);
            _store.Commit(new PackInstallTransaction(NodeTenantFor(), producer,
                new PackInstallWatermark(key, "1.0.0", new Dictionary<string, int>()), []));
        }
        var before = JsonSerializer.Serialize(_store.ListInstalled(NodeTenantFor()));
        var content = BookingContent("Resource", "acme.surface");
        content["from_type_id"] = "acme.room";
        var dependencies = new[] { new { key = "acme.first", version = "1.0.0" }, new { key = "acme.second", version = "1.0.0" } };
        if (reverse) Array.Reverse(dependencies);
        var pack = new { key = "acme.contract", version = "1.0.0", name = "Acme Contract",
            description = "contract window pack", scopeTier = "Vertical", dependencies,
            contents = new[] { new { key = "acme.surface", kind = "Resource", version = "1.0.0", content } } };
        var bytes = await ExportAsync(pack);
        var file = Assert.IsType<PackFile>(new PackFileCodec().TryDecode(bytes));
        var envelope = Assert.IsType<SignedOperation<PackSignatureSubject>>(file.Envelope);
        Assert.Equal(reverse ? new[] { "acme.second", "acme.first" } : new[] { "acme.first", "acme.second" },
            envelope.Payload.Manifest.Dependencies.Select(dependency => dependency.Key));
        using var response = await PostBytesAsync(PackInstallRoutes.InstallRoute, bytes);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("installed").GetBoolean());
        Assert.Equal("install", body.GetProperty("stage").GetString());
        var refusal = Assert.Single(body.GetProperty("refusals").EnumerateArray());
        Assert.Equal("booking.definition.type_unknown", refusal.GetProperty("code").GetString());
        Assert.Equal("/from_type_id", refusal.GetProperty("pointer").GetString());
        Assert.Equal("acme.surface@1.0.0", refusal.GetProperty("target").GetString());
        Assert.Equal(before, JsonSerializer.Serialize(_store.ListInstalled(NodeTenantFor())));
    }

    [Fact(DisplayName = "T-1034 booking-auth-20: a malformed exposed type fails closed")]
    public async Task Malformed_exposed_record_type_is_unknown()
    {
        var record = BookingRecordType(true);
        record["traits"] = new JsonArray((JsonNode?)null);
        SeedBookingType(record);
        using var response = await PostBytesAsync(PackInstallRoutes.InstallRoute,
            await ExportAsync(PackBody("Resource", new { major = 1, minor = 0 })));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("installed").GetBoolean());
        Assert.Equal("install", body.GetProperty("stage").GetString());
        var refusal = Assert.Single(body.GetProperty("refusals").EnumerateArray());
        Assert.Equal("booking.definition.type_unknown", refusal.GetProperty("code").GetString());
        Assert.Equal("/from_type_id", refusal.GetProperty("pointer").GetString());
        Assert.Equal("acme.surface@1.0.0", refusal.GetProperty("target").GetString());
        Assert.Single(_store.ListInstalled(NodeTenantFor()));
    }

    [Fact(DisplayName = "T-1034 DES-0014 C3: Check preserves Booking refusals before reattachment")]
    public async Task Check_booking_refusal_precedes_reattachment_refusal()
    {
        SeedBookingType();
        var oldContent = BookingContent("Resource", "acme.surface");
        oldContent["from_type_id"] = "acme.room";
        var oldJson = oldContent.ToJsonString();
        var seed = new PackSeedItem("acme.surface", PackContentKind.Resource, "1.0.0", oldJson,
            Cid.FromBytes(Encoding.UTF8.GetBytes(oldJson)));
        var floorJson = """{"name":"c","safetyFloors":{"scoring":3}}""";
        var floorSeed = new PackSeedItem("acme.floor", PackContentKind.StandardsCatalog, "1.0.0", floorJson, Cid.FromBytes(Encoding.UTF8.GetBytes(floorJson)));
        var prior = RecordsInstallClosureGateTests.Producer("acme.contract", "1.0.0", [seed, floorSeed], [], []);
        var tenant = NodeTenantFor();
        _store.Commit(new PackInstallTransaction(tenant, prior,
            new PackInstallWatermark("acme.contract", "1.0.0", new Dictionary<string, int> { ["scoring"] = 3 }), []));
        _store.SaveOverride(tenant, "acme.contract", new PackTenantOverride("acme.floor",
            JsonNode.Parse("""{"safetyFloors":{"scoring":"bad"}}""")!));
        var before = JsonSerializer.Serialize(_store.GetOverrides(tenant, "acme.contract"));
        async Task<JsonElement> Check(string type)
        {
            var content = BookingContent("Resource", "acme.surface");
            content["envelope"]!["version"] = "2.0.0";
            content["from_type_id"] = type;
            var pack = new { key = "acme.contract", version = "2.0.0", name = "Acme Contract",
                description = "contract window pack", scopeTier = "Vertical",
                dependencies = new[] { new { key = "acme.producer", version = "1.0.0" } },
                contents = new object[] { new { key = "acme.surface", kind = "Resource", version = "2.0.0", content },
                    new { key = "acme.floor", kind = "StandardsCatalog", version = "2.0.0", content = new { name = "c", safetyFloors = new { scoring = 3 } } } } };
            using var response = await PostBytesAsync(PackInstallRoutes.CheckRoute, await ExportAsync(pack));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }
        var control = await Check("acme.room");
        Assert.Equal("platform-package-safety-floor-malformed", Assert.Single(control.GetProperty("refusals").EnumerateArray()).GetProperty("code").GetString());
        var body = await Check("acme.unknown");
        Assert.Equal("Refused", body.GetProperty("verdict").GetString());
        var refusal = Assert.Single(body.GetProperty("refusals").EnumerateArray(), item =>
            item.GetProperty("code").GetString() == "booking.definition.type_unknown");
        Assert.Equal("booking.definition.type_unknown", refusal.GetProperty("code").GetString());
        Assert.Equal("/from_type_id", refusal.GetProperty("pointer").GetString());
        Assert.Equal("acme.surface@2.0.0", refusal.GetProperty("target").GetString());
        Assert.Equal(before, JsonSerializer.Serialize(_store.GetOverrides(tenant, "acme.contract")));
        Assert.Null(_store.GetVersion(tenant, "acme.contract", "2.0.0"));
    }

    [Fact(DisplayName = "T-1034 DES-0014 C3: Check collects Booking and dependency refusals without duplicating the contract refusal")]
    public async Task Check_collects_booking_refusals_once()
    {
        using (var seed = await PostBytesAsync(PackInstallRoutes.InstallRoute,
            await ExportAsync(PackBody("Layout", new { major = 1, minor = 0 }, key: "acme.seed"))))
            Assert.Equal(HttpStatusCode.OK, seed.StatusCode);
        var content = BookingContent("Bookable", "acme.surface");
        content["envelope"]!["contract"]!["major"] = 2;
        content["requires"] = new JsonArray("acme.missing-resource");
        var pack = new { key = "acme.contract", version = "1.0.0", name = "Acme Contract",
            description = "contract window pack", scopeTier = "Vertical",
            dependencies = new[] { new { key = "acme.missing", version = "1.0.0" } },
            contents = new[] { new { key = "acme.surface", kind = "Bookable", version = "1.0.0", content } } };
        using var response = await PostBytesAsync(PackInstallRoutes.CheckRoute, await ExportAsync(pack));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Refused", body.GetProperty("verdict").GetString());
        var refusals = body.GetProperty("refusals").EnumerateArray().ToArray();
        Assert.Equal(4, refusals.Length);
        Assert.Equal("definition.contract.out_of_window", refusals[0].GetProperty("code").GetString());
        Assert.Equal("/envelope/contract", refusals[0].GetProperty("pointer").GetString());
        Assert.Equal("acme.surface@1.0.0", refusals[0].GetProperty("target").GetString());
        Assert.Equal("booking.definition.type_unknown", refusals[1].GetProperty("code").GetString());
        Assert.Equal("/on_type_id", refusals[1].GetProperty("pointer").GetString());
        Assert.Equal("acme.surface@1.0.0", refusals[1].GetProperty("target").GetString());
        Assert.Equal("booking.bookable.required_resource_unknown", refusals[2].GetProperty("code").GetString());
        Assert.Equal("/requires/0", refusals[2].GetProperty("pointer").GetString());
        Assert.Equal("acme.surface@1.0.0", refusals[2].GetProperty("target").GetString());
        Assert.Equal("pack.install.refused.unmet_dependency", refusals[3].GetProperty("code").GetString());
        Assert.Equal("/dependencies/0", refusals[3].GetProperty("pointer").GetString());
        Assert.DoesNotContain(_store.ListInstalled(NodeTenantFor()), pack => pack.PackKey == "acme.contract");
    }

    [Fact(DisplayName = "T-1034 DES-0025 section 7: a Resource and its Bookable install together")]
    public async Task Resource_and_requiring_bookable_install_together()
    {
        SeedBookingType();
        var resource = BookingContent("Resource", "acme.resource");
        resource["from_type_id"] = "acme.room";
        var bookable = BookingContent("Bookable", "acme.bookable");
        bookable["on_type_id"] = "acme.room";
        bookable["requires"] = new JsonArray("acme.resource");
        var pack = new { key = "acme.contract", version = "1.0.0", name = "Acme Contract",
            description = "contract window pack", scopeTier = "Vertical",
            dependencies = new[] { new { key = "acme.producer", version = "1.0.0" } },
            contents = new object[] { new { key = "acme.resource", kind = "Resource", version = "1.0.0", content = resource },
                new { key = "acme.bookable", kind = "Bookable", version = "1.0.0", content = bookable } } };
        using var response = await PostBytesAsync(PackInstallRoutes.InstallRoute, await ExportAsync(pack));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("installed").GetBoolean());
        var installed = _store.GetVersion(NodeTenantFor(), "acme.contract", "1.0.0");
        Assert.NotNull(installed);
        Assert.Contains(installed.SeedItems, item => item.Key == "acme.resource" && (int)item.Kind == 18);
        Assert.Contains(installed.SeedItems, item => item.Key == "acme.bookable" && (int)item.Kind == 19);
    }

    [Fact(DisplayName = "T-1034 DES-0025 section 7: a Booking refusal preserves installed state")]
    public async Task Booking_refusal_preserves_the_installed_state()
    {
        SeedBookingType();
        using (var initial = await PostBytesAsync(PackInstallRoutes.InstallRoute,
            await ExportAsync(PackBody("Resource", new { major = 1, minor = 0 }))))
            Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        var tenant = NodeTenantFor();
        _store.Activate(tenant, "acme.contract", "1.0.0");
        _store.SaveOverride(tenant, "acme.contract", new PackTenantOverride("acme.surface", new JsonObject { ["name"] = "Tenant room" }));
        var installed = JsonSerializer.Serialize(_store.ListInstalled(tenant));
        var active = JsonSerializer.Serialize(_store.GetActive(tenant, "acme.contract"));
        var watermark = JsonSerializer.Serialize(_store.GetWatermark(tenant, "acme.contract"));
        var overrides = JsonSerializer.Serialize(_store.GetOverrides(tenant, "acme.contract"));
        var content = BookingWindowContent("Resource", new { major = 1, minor = 0 });
        content["envelope"]!["version"] = "2.0.0";
        content["from_type_id"] = "acme.unknown";
        var pack = new { key = "acme.contract", version = "2.0.0", name = "Acme Contract", description = "contract window pack",
            scopeTier = "Vertical", dependencies = new[] { new { key = "acme.producer", version = "1.0.0" } },
            contents = new[] { new { key = "acme.surface", kind = "Resource", version = "2.0.0", content } } };
        using var response = await PostBytesAsync(PackInstallRoutes.InstallRoute, await ExportAsync(pack));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var refusal = Assert.Single(body.GetProperty("refusals").EnumerateArray());
        Assert.Equal("booking.definition.type_unknown", refusal.GetProperty("code").GetString());
        Assert.Equal("/from_type_id", refusal.GetProperty("pointer").GetString());
        Assert.Equal("acme.surface@2.0.0", refusal.GetProperty("target").GetString());
        Assert.Equal(installed, JsonSerializer.Serialize(_store.ListInstalled(tenant)));
        Assert.Equal(active, JsonSerializer.Serialize(_store.GetActive(tenant, "acme.contract")));
        Assert.Equal(watermark, JsonSerializer.Serialize(_store.GetWatermark(tenant, "acme.contract")));
        Assert.Equal(overrides, JsonSerializer.Serialize(_store.GetOverrides(tenant, "acme.contract")));
    }

    internal static JsonObject BookingContent(string kind, string key) => new()
    {
        ["kind"] = kind == "Resource" ? "resource" : "bookable",
        ["envelope"] = new JsonObject
        {
            ["identity"] = key, ["version"] = "1.0.0", ["tenant"] = NodeTenantFor().Value,
            ["cascade_layer"] = "domain_package", ["provenance"] = new JsonObject { ["kind"] = "package", ["package"] = "acme" },
            ["retention_class"] = "definition", ["legal_hold"] = false, ["requires"] = new JsonArray(),
            ["contract"] = new JsonObject { ["major"] = 1, ["minor"] = 0 },
        },
        ["name"] = "Surface",
        [kind == "Resource" ? "from_type_id" : "on_type_id"] = "acme.unknown",
        [kind == "Resource" ? "capacity_kind" : "duration_intervals_minutes"] = kind == "Resource" ? JsonValue.Create("exclusive") : new JsonArray(90),
        [kind == "Resource" ? "availability_from" : "requires"] = kind == "Resource" ? JsonValue.Create("supply.hours") : new JsonArray(),
    };

    [Theory(DisplayName = "T-1034 kernel-core-ck-8: Booking without a declared contract is refused by platform admission")]
    [InlineData("Resource")]
    [InlineData("Bookable")]
    public async Task Booking_without_a_declared_contract_is_refused(string kind)
    {
        SeedBookingType();
        using var response = await PostBytesAsync(PackInstallRoutes.InstallRoute,
            await ExportAsync(PackBody(kind, contract: null)));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("installed").GetBoolean());
        Assert.Equal("install", body.GetProperty("stage").GetString());
        var refusal = Assert.Single(body.GetProperty("refusals").EnumerateArray());
        Assert.Equal("definition.contract.missing", refusal.GetProperty("code").GetString());
        Assert.Equal("/envelope/contract", refusal.GetProperty("pointer").GetString());
        Assert.Equal("acme.surface@1.0.0", refusal.GetProperty("target").GetString());
        Assert.DoesNotContain(_store.ListInstalled(NodeTenantFor()), pack => pack.PackKey == "acme.contract");
    }

    [Fact]
    public async Task Layout_without_a_declared_contract_is_refused_by_platform_admission()
    {
        using var response = await PostBytesAsync(PackInstallRoutes.InstallRoute,
            await ExportAsync(PackBody("Layout", contract: null)));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("installed").GetBoolean());
        Assert.Equal("install", body.GetProperty("stage").GetString());
        var refusal = Assert.Single(body.GetProperty("refusals").EnumerateArray());
        Assert.Equal("definition.contract.missing", refusal.GetProperty("code").GetString());
        Assert.Equal("/envelope/contract", refusal.GetProperty("pointer").GetString());
        Assert.Equal("acme.surface@1.0.0", refusal.GetProperty("target").GetString());
        Assert.Empty(_store.ListInstalled(NodeTenantFor()));
    }

    [Fact(DisplayName = "T-572 S4: check collects the contract refusal with the pack's other refusals and installs nothing")]
    [Trait("Holds", "kernel-core-ck-8")]
    public async Task Check_collects_the_contract_refusal()
    {
        // Check is exposed once something is installed.
        using (var seed = await PostBytesAsync(PackInstallRoutes.InstallRoute, await ExportAsync(
            PackBody("Layout", new { major = 1, minor = 0 }, key: "acme.seed"))))
        {
            Assert.Equal(HttpStatusCode.OK, seed.StatusCode);
        }

        using var response = await PostBytesAsync(PackInstallRoutes.CheckRoute,
            await ExportAsync(PackBody("Layout", new { major = 2, minor = 0 })));

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
    [InlineData(PackContentKind.WorkflowDefinition, 2, 0, "acme.contract.workflow@1.0.0")]
    [InlineData(PackContentKind.WorkflowDefinition, 1, 1, "acme.contract.workflow@1.0.0")]
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

    [Theory(DisplayName = "T-1033: workflows install with literal 1.0 or the interim omitted declaration")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Install_valid_workflow_definitions(bool declaresContract)
    {
        var declared = declaresContract ? new DefinitionContractVersion(1, 0) : null;
        var content = NativeContent(PackContentKind.WorkflowDefinition, declared);
        AssertNativeMetadata(PackContentKind.WorkflowDefinition, content, declared);

        using var response = await PostBytesAsync(PackInstallRoutes.InstallRoute,
            await ExportAsync(NativePackBody(PackContentKind.WorkflowDefinition, content)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("installed").GetBoolean());
        Assert.Equal("acme.contract", Assert.Single(_store.ListInstalled(NodeTenantFor())).PackKey);
    }

    [Fact(DisplayName = "T-1033: every current pack kind has an explicit wire classification and declared fixtures use the real check")]
    public void New_pack_kinds_require_an_explicit_wire_fixture_or_documented_boundary()
    {
        // This is a wire inventory, independent of the check's supported-kind selection.
        var fixtures = new Dictionary<PackContentKind, (string Shape, string? Key, JsonObject? Body)>
        {
            [PackContentKind.FormDefinition] = ("definitionEnvelope.contract", "acme.contract.form", NativeContent(PackContentKind.FormDefinition, new(2, 0))),
            [PackContentKind.WorkflowDefinition] = ("envelope.contract", "acme.contract.workflow", NativeContent(PackContentKind.WorkflowDefinition, new(2, 0))),
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
            [PackContentKind.Layout] = ("envelope.contract", "acme.surface", LayoutContent(new { major = 2, minor = 0 })),
            [PackContentKind.Resource] = ("envelope.contract", "acme.surface", PlatformWindowContent()),
            [PackContentKind.Bookable] = ("envelope.contract", "acme.surface", PlatformWindowContent()),
            [PackContentKind.ReleasedNavigationDefinition] = ("released navigation wire; refused as unsupported until T-584/api-installs-released-navigation", null, null),
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

    [Fact(DisplayName = "T-1033: the check reads the canonical workflow contract even though the lean model omits it")]
    public void Workflow_contract_is_checked_from_canonical_wire_not_the_lean_model()
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
        var refusal = Assert.Single(PackContractWindowCheck.FindRefusals([item], new DefinitionContractWindow(1, 0, 1)));
        Assert.Equal("definition.contract.out_of_window", refusal.Code);
        Assert.Equal("/envelope/contract", refusal.Pointer);
        Assert.Equal("acme.contract.workflow@1.0.0", refusal.Target);
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
        PackContentKind.WorkflowDefinition => "acme.contract.workflow",
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
            PackContentKind.WorkflowDefinition => new
            {
                key, version = "1.0.0", tenant = NodeTenantFor().Value, envelope = NativeEnvelope(key, contract),
                initialState = "Done", states = new[] { new { id = "Done", kind = "Terminal" } },
                transitions = Array.Empty<object>(), triggers = Array.Empty<object>(), actions = Array.Empty<object>(),
            },
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
            PackContentKind.WorkflowDefinition => content["envelope"]!["contract"]?.Deserialize<DefinitionContractVersion>(Json),
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
        var contract = body.GetProperty("contract");
        Assert.Equal(1, contract.GetProperty("app_major").GetInt32());
        Assert.Equal(0, contract.GetProperty("app_minor").GetInt32());
        Assert.Equal([1, 1],
            contract.GetProperty("window").EnumerateArray().Select(edge => edge.GetInt32()));
    }

    private async Task<byte[]> ExportAsync(object body)
    {
        using var response = await _client.PostAsJsonAsync(PackComposerRoutes.ExportRoute, body);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
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
                content = kind == "Layout" ? LayoutContent(contract) : BookingWindowContent(kind, contract),
            },
        },
        dependencies = kind == "Layout" ? Array.Empty<object>() : [new { key = "acme.producer", version = "1.0.0" }],
    };

    private static JsonObject BookingWindowContent(string kind, object? contract)
    {
        var content = BookingContent(kind, "acme.surface");
        content[kind == "Resource" ? "from_type_id" : "on_type_id"] = "acme.room";
        content["envelope"]!["contract"] = JsonSerializer.SerializeToNode(contract);
        return content;
    }

    private void SeedBookingType(JsonObject? record = null)
    {
        record ??= BookingRecordType(true, "acme.producer");
        record["envelope"]!["package_id"] = "acme.producer";
        var body = record.ToJsonString();
        var seed = new PackSeedItem("acme.type", PackContentKind.AssetTypeDefinition, "1.0.0", body,
            Cid.FromBytes(Encoding.UTF8.GetBytes(body)));
        var producer = RecordsInstallClosureGateTests.Producer("acme.producer", "1.0.0", [seed], ["acme.type"],
            [new PackExposedDefinition("acme.type", "1.0.0", "sha256:" + new string('a', 64))]);
        _store.Commit(new PackInstallTransaction(NodeTenantFor(), producer,
            new PackInstallWatermark("acme.producer", "1.0.0", new Dictionary<string, int>()), []));
    }

    private static JsonObject LayoutContent(object? contract)
    {
        var content = JsonNode.Parse(LayoutDefinitionJson.SerializeCanonical(new LayoutDefinition(
            new LayoutDefinitionEnvelope("acme.surface", "1.0.0", NodeTenantFor().Value, LayoutCascadeLayer.TenantConfiguration,
                JsonSerializer.SerializeToElement(new { source = "contract-window" }), "standard", false,
                [new LayoutDefinitionRequirement("platform.layout", "1.0.0")], new DefinitionContractVersion(1, 0)),
            1, LayoutMedium.Screen, LayoutIntent.Observe,
            [new LayoutBlock("title", "layout.text", new LayoutStaticBinding(JsonSerializer.SerializeToElement("Surface")), [])],
            [], [], [], null, [])))!.AsObject();
        content["envelope"]!["contract"] = JsonSerializer.SerializeToNode(contract);
        return content;
    }

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
