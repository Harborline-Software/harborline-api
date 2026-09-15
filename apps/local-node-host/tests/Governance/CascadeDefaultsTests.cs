using System.Text;
using System.Text.Json.Nodes;
using Harborline.Api.Blocks.Assets.Registry.DependencyInjection;
using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Blobs;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.Governance.Enforcement;
using Harborline.Api.Foundation.Governance.Policy;
using Harborline.Api.Foundation.Governance.Resolution;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Admission;
using Harborline.Api.Foundation.Packs.Install.Trust;
using Harborline.Api.Foundation.Packs.Install.Audit;
using Harborline.Api.Foundation.Packs.Export;
using Harborline.Api.Foundation.Packs.Dcp;
using Harborline.Api.Foundation.Packs.Serialization;
using Harborline.Api.Foundation.Packs.Validation;
using Harborline.Api.Foundation.Packs.Verify;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Kernel.Schema;
using Harborline.Api.LocalNodeHost.Data.PackProjection;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Authorization;
using Harborline.Api.LocalNodeHost.Tests.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Governance;

public sealed class CascadeDefaultsTests
{
    private static readonly TenantId Tenant = new("defaults-tenant");
    private const string Package = "tests.defaults";
    internal const string Body = """
        {"schemaVersion":1,"title":"Governance defaults","defaults":[
          {"classification":[],"personalData":false,"masking":{"revealLast":0},
           "retention":{"regime":"GDPR","floorClass":"Identity","minimumRetentionDays":30},
           "conflictPolicy":"ask","trackChanges":true},
          {"recordType":"contact","masking":{"revealLast":2}},
          {"recordType":"contact","field":"title","masking":{"revealLast":4},"trackChanges":false}]}
        """;

    [Theory]
    [InlineData("\"schemaVersion\":1", "\"schemaVersion\":2", "pack.defaults.unsupported_version")]
    [InlineData("\"schemaVersion\":1", "\"schemaVersion\":\"1\"", "pack.defaults.malformed")]
    [InlineData("\"title\":\"Governance defaults\"", "\"surprise\":true", "pack.defaults.malformed")]
    [InlineData("\"revealLast\":4", "\"revealLast\":-1", "pack.defaults.malformed")]
    [InlineData("\"revealLast\":4", "", "pack.defaults.malformed")]
    [InlineData("\"conflictPolicy\":\"ask\"", "\"conflictPolicy\":\"last-writer-wins\"", "pack.defaults.malformed")]
    [InlineData("\"recordType\":\"contact\",\"field\":\"title\"", "\"field\":\"title\"", "pack.defaults.malformed")]
    [InlineData("\"field\":\"title\",", "", "pack.defaults.malformed")]
    [InlineData("\"trackChanges\":false", "\"trackChanges\":false,\"trackChanges\":true", "pack.defaults.malformed")]
    [InlineData("\"floorClass\":\"Identity\"", "\"floorClass\":\"unknown\"", "pack.defaults.malformed")]
    public void Admission_rejects_unknown_or_malformed_contract(string before, string after, string code)
    {
        var adapter = new PackWorkflowAdmissionAdapter(Substitute.For<IWorkflowAdmissionValidator>(), defaults: new());
        var refusal = Assert.Single(adapter.Admit([new(Package, "defaults", PackContentKind.CascadeDefaults,
            "1.0.0", Body.Replace(before, after, StringComparison.Ordinal))], Tenant).Refusals);
        Assert.Equal(code, refusal.Code);
        Assert.StartsWith("/", refusal.Pointer);
    }

    [Fact]
    public void Admission_requires_consumer_and_rejects_cross_item_duplicate_coordinates()
    {
        var item = new PackComposedItem(Package, "defaults", PackContentKind.CascadeDefaults, "1.0.0", Body);
        Assert.Equal(PackAdmissionCodes.NotWired, Assert.Single(new WorkflowRefusingPackContentAdmission().Admit([item], Tenant).Refusals).Code);
        Assert.Equal(PackAdmissionCodes.NotWired, Assert.Single(new PackWorkflowAdmissionAdapter(Substitute.For<IWorkflowAdmissionValidator>()).Admit([item], Tenant).Refusals).Code);
        var adapter = new PackWorkflowAdmissionAdapter(Substitute.For<IWorkflowAdmissionValidator>(), defaults: new());
        Assert.True(adapter.Admit([item], Tenant).IsAdmissible);
        Assert.Equal(PackCascadeDefaultsContent.Malformed, Assert.Single(adapter.Admit([item, item with { Key = "other" }], Tenant).Refusals).Code);
    }

    [Theory]
    [InlineData("\"schemaVersion\":1", "\"schemaVersion\":2", "pack.defaults.unsupported_version")]
    [InlineData("\"trackChanges\":false", "\"unknownAxis\":true", "pack.defaults.malformed")]
    public async Task Signed_install_admits_v1_and_refuses_invalid_upgrade_before_mutation(string before, string after, string code)
    {
        using var keys = KeyPair.Generate();
        var codec = new PackFileCodec();
        var store = new InMemoryPackInstallStore();
        var installer = new PackInstaller(new PackVerifier(new Ed25519Verifier(), codec), store,
            new PackWorkflowAdmissionAdapter(Substitute.For<IWorkflowAdmissionValidator>(), defaults: new()),
            new InMemoryPackInstallAudit(), TestAuthorization.AllowGate());
        var context = new PackInstallContext(Tenant,
            new InMemoryPackTrustStore([new PackTrustRoot(TrustScope.OwnRoster, keys.PrincipalId, 1, TrustRootStatus.Current)]),
            PackRevocationList.Empty, TimeProvider.System.GetUtcNow(), TimeSpan.FromDays(30), Principal: "test-operator");
        var exporter = new PackExporter(new PackContentCanonicalizer(), new PackDcpCanonicalizer(),
            new PackValidator(new PackContentPiiScanner()), new DcpValidator(DcpCounselRegister.FromEmbeddedResource()), codec, timeProvider: TimeProvider.System);
        async Task<byte[]> Export(string version, string body)
        {
            var exported = await exporter.ExportAsync(new PackExportRequest(Package, version, "Defaults", "Governance admission",
                PackScopeTier.Vertical, [new PackContentSource("defaults", PackContentKind.CascadeDefaults, version, JsonNode.Parse(body)!)],
                [], [], Epoch: 1, Dcp: DomainComplianceProfile.General("test-author")), new Ed25519Signer(keys));
            Assert.True(exported.Succeeded, string.Join(";", exported.Validation.Errors.Select(error => error.Message)));
            return exported.FileBytes!;
        }
        Assert.True(installer.Install(await Export("1.0.0", Body), context).Installed);
        var prior = Assert.Single(store.ListInstalled(Tenant));
        var watermark = store.GetWatermark(Tenant, Package);
        var refused = installer.Install(await Export("1.1.0", Body.Replace(before, after, StringComparison.Ordinal)), context);
        Assert.False(refused.Installed);
        Assert.Equal(code, Assert.Single(refused.Preview.AdmissionRefusals).Code);
        Assert.StartsWith("/contents/0", Assert.Single(refused.Preview.Refusals).Pointer);
        Assert.Equal(prior, Assert.Single(store.ListInstalled(Tenant)));
        Assert.Equal(watermark, store.GetWatermark(Tenant, Package));
        Assert.Empty(store.GetOverrides(Tenant, Package));
    }

    [Fact]
    public async Task Active_projection_resolves_six_axes_and_changes_real_read_masking()
    {
        using var fixture = new Fixture();
        fixture.Install("1.0.0", Body, includeForm: true);
        Assert.Empty(fixture.Defaults.List(Tenant));
        fixture.Packs.Activate(Tenant, Package, "1.0.0");
        Assert.Empty((await fixture.Projector.ProjectActivePacksAsync(Tenant)).Refusals);
        var values = fixture.Defaults.Resolve(Tenant, Package, "contact", "title");
        Assert.Equal(4, values.Values.Masking!.RevealLast);
        Assert.False(values.Values.TrackChanges);
        Assert.False(values.Values.PersonalData);
        Assert.Equal("ask", values.Values.ConflictPolicy);
        Assert.Equal(30, values.Values.Retention!.MinimumRetentionDays);
        Assert.Empty(values.Values.Classification!);
        Assert.Equal(6, values.Sources.Count);
        Assert.All(values.Sources.Values, source => Assert.Equal(Tenant, source.Tenant));
        Assert.Equal(2, fixture.Defaults.Resolve(Tenant, Package, "contact", "formId").Values.Masking!.RevealLast);
        Assert.Equal(0, fixture.Defaults.Resolve(Tenant, Package, "other", "title").Values.Masking!.RevealLast);
        Assert.Null(fixture.Defaults.Resolve(new("other-tenant"), Package, "contact", "title").Values.Masking);
        var form = await fixture.Forms.GetAsync(new(Tenant, "contact", "1.0.0"));
        var resolver = new AspectResolver(new InMemoryPolicyRegistry(), fixture.Defaults);
        var policy = resolver.ResolvePolicy(form, "title");
        Assert.Equal(4, policy.Effect(EffectKind.Mask, Trigger.Read)!.Params.MaskRevealLast);
        var services = new ServiceCollection();
        services.AddSingleton<Harborline.Api.Foundation.Recovery.TenantKey.ITenantKeyProvider,
            Harborline.Api.Foundation.Recovery.TenantKey.InMemoryTenantKeyProvider>();
        services.AddSingleton<Harborline.Api.Foundation.Recovery.Crypto.IFieldEncryptor,
            Harborline.Api.Foundation.Recovery.Crypto.TenantKeyProviderFieldEncryptor>();
        services.AddTestAuthorizationGate().AddTestNodeForms();
        using var provider = services.BuildServiceProvider();
        var enforcer = provider.GetRequiredService<IFieldPolicyEnforcer>();
        var read = await enforcer.ProjectForReadAsync(new(policy, "12345678", Tenant, new("reader"), new("harborline", "node", "contact"), ["tax.roles/default-reader"]));
        Assert.True(read.Masked);
        Assert.Equal("****5678", read.Value);
        var unauthorized = await enforcer.ProjectForReadAsync(new(policy, "12345678", Tenant, new("reader"), new("harborline", "node", "contact"), []));
        Assert.False(unauthorized.Readable);
        Assert.Null(unauthorized.Value);
        fixture.Packs.Deactivate(Tenant, Package, "1.0.0");
        await fixture.Projector.ProjectActivePacksAsync(Tenant);
        Assert.Empty(fixture.Defaults.List(Tenant));
        Assert.False(resolver.ResolvePolicy(form, "title").Has(EffectKind.Mask, Trigger.Read));
        fixture.Packs.Activate(Tenant, Package, "1.0.0");
        await fixture.Projector.ProjectActivePacksAsync(Tenant);
        Assert.Equal(4, resolver.ResolvePolicy(form, "title").Effect(EffectKind.Mask, Trigger.Read)!.Params.MaskRevealLast);
    }

    [Fact]
    public async Task Overrides_upgrade_ownership_and_catalogue_follow_the_active_projection()
    {
        using var fixture = new Fixture();
        fixture.Install("1.0.0", Body);
        fixture.Packs.Activate(Tenant, Package, "1.0.0");
        var declarations = JsonNode.Parse(Body)!["defaults"]!.DeepClone().AsArray();
        declarations.RemoveAt(2);
        var patch = new JsonObject { ["defaults"] = declarations };
        var installer = new PackInstaller(Substitute.For<IPackVerifier>(), fixture.Packs,
            new PackWorkflowAdmissionAdapter(Substitute.For<IWorkflowAdmissionValidator>(), defaults: fixture.Defaults),
            new InMemoryPackInstallAudit(), TestAuthorization.AllowGate());
        var context = new PackInstallContext(Tenant, new InMemoryPackTrustStore([]), PackRevocationList.Empty,
            TestAuthorization.At, TimeSpan.FromDays(30), Principal: "test-operator");
        var decision = TestAuthorization.AllowedDecision(Tenant, Package, "pack", Permission.PackagesOperate);
        Assert.False(installer.Narrow(context, Package, "defaults", JsonNode.Parse("""{"schemaVersion":null}""")!, decision).Recorded);
        Assert.Empty(fixture.Packs.GetOverrides(Tenant, Package));
        Assert.True(installer.Narrow(context, Package, "defaults", patch, decision).Recorded);
        await fixture.Projector.ProjectActivePacksAsync(Tenant);
        Assert.Equal(2, fixture.Defaults.Resolve(Tenant, Package, "contact", "title").Values.Masking!.RevealLast);
        Assert.True(Assert.Single(fixture.Defaults.List(Tenant)).Source.TenantOverride);
        fixture.Install("1.1.0", Body.Replace("\"revealLast\":4", "\"revealLast\":3", StringComparison.Ordinal));
        fixture.Packs.Activate(Tenant, Package, "1.1.0");
        await fixture.Projector.ProjectActivePacksAsync(Tenant);
        Assert.Equal("1.1.0", Assert.Single(fixture.Defaults.List(Tenant)).Source.PackVersion);
        Assert.Equal(3, fixture.Defaults.Resolve(Tenant, Package, "contact", "title").Values.Masking!.RevealLast);
        var catalogue = new CatalogueRegistries(fixture.Packs, defaults: fixture.Defaults);
        Assert.True(catalogue.IsAvailable(PackContentKind.CascadeDefaults));
        Assert.False(new CatalogueRegistries(fixture.Packs).IsAvailable(PackContentKind.CascadeDefaults));
        Assert.Single(await catalogue.ReadAsync(Tenant, PackContentKind.CascadeDefaults));
        Assert.Empty(await catalogue.ReadAsync(new("other"), PackContentKind.CascadeDefaults));
        fixture.Install("1.0.0", Body, package: "contender");
        fixture.Packs.Activate(Tenant, "contender", "1.0.0");
        await fixture.Projector.ProjectActivePacksAsync(Tenant);
        Assert.Empty(fixture.Defaults.List(Tenant));
        fixture.Packs.RecordKeyOwnership(Tenant, "defaults", "contender");
        await fixture.Projector.ProjectActivePacksAsync(Tenant);
        Assert.Equal("contender", Assert.Single(fixture.Defaults.List(Tenant)).Source.PackId);
    }

    [Fact]
    public async Task Catalogue_reads_projection_without_inspecting_install_store_and_distinguishes_empty()
    {
        var store = Substitute.For<IPackInstallStore>();
        var projection = new ActiveCascadeDefaultsProjection();
        var catalogue = new CatalogueRegistries(store, defaults: projection);
        Assert.True(catalogue.IsAvailable(PackContentKind.CascadeDefaults));
        Assert.Empty(await catalogue.ReadAsync(Tenant, PackContentKind.CascadeDefaults));
        store.DidNotReceive().ListInstalled(Arg.Any<TenantId>());
        Assert.False(new CatalogueRegistries(store).IsAvailable(PackContentKind.CascadeDefaults));
    }

    [Fact]
    public async Task Unsupported_legacy_projection_is_refused_and_retracts_superseded_values()
    {
        using var fixture = new Fixture();
        fixture.Install("1.0.0", Body);
        fixture.Packs.Activate(Tenant, Package, "1.0.0");
        await fixture.Projector.ProjectActivePacksAsync(Tenant);
        fixture.Install("1.1.0", Body.Replace("\"schemaVersion\":1", "\"schemaVersion\":2", StringComparison.Ordinal));
        fixture.Packs.Activate(Tenant, Package, "1.1.0");
        var refusal = Assert.Single((await fixture.Projector.ProjectActivePacksAsync(Tenant)).Refusals);
        Assert.Equal(PackCascadeDefaultsContent.UnsupportedVersion, refusal.Code);
        Assert.EndsWith("/schemaVersion", refusal.Pointer);
        Assert.Empty(fixture.Defaults.List(Tenant));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly ServiceProvider services = new ServiceCollection().AddLogging().AddInMemoryAssetTypeSystem().BuildServiceProvider();
        public readonly InMemoryPackInstallStore Packs = new();
        public readonly ActiveCascadeDefaultsProjection Defaults = new();
        public readonly InMemoryFormDefinitionStore Forms = new(TimeProvider.System);
        public PackSeedProjector Projector { get; }
        public Fixture() => Projector = new(Packs, services.GetRequiredService<IEntityTypeRegistry>(),
            NullLogger<PackSeedProjector>.Instance, time: TimeProvider.System, defaults: Defaults,
            forms: Forms, schemas: new InMemorySchemaRegistry(TimeProvider.System),
            authorizedForms: TestAuthorization.FormLifecycle(Forms, TestAuthorization.AllowGate(), new RoleGateAdmission(
                new InMemoryRoleVocabulary([RoleDefinition.CreateTenantRole(RoleDefinitionId.New(), "default-reader", "Reader", Tenant)]))));
        public void Install(string version, string body, bool includeForm = false, string package = Package)
        {
            var items = new List<PackSeedItem> { new("defaults", PackContentKind.CascadeDefaults, version, body, Cid.FromBytes(Encoding.UTF8.GetBytes(body))) };
            if (includeForm)
            {
                var content = CatalogueFieldSourceContractTests.Content(false);
                content["overlay"]!["sections"]![0]!["access"] = JsonNode.Parse("""{"readRoles":["tax.roles/default-reader"],"writeRoles":[]}""");
                var form = content.ToJsonString();
                items.Add(new("contact", PackContentKind.FormDefinition, "1.0.0", form, Cid.FromBytes(Encoding.UTF8.GetBytes(form))));
            }
            var pack = new InstalledPack(package, version, PackScopeTier.Horizontal, PackLifecycleState.Draft,
                items, new Dictionary<string, int>(), TestAuthorization.At, PrincipalId.FromBytes(new byte[PrincipalId.LengthInBytes]), 1, TrustScope.OwnRoster, []);
            Packs.Commit(new(Tenant, pack, new(package, version, new Dictionary<string, int>()), []));
        }
        public void Dispose() { Forms.Dispose(); services.Dispose(); }
    }
}
