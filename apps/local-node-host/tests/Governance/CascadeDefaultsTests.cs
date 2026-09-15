using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json;
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
using Harborline.Api.Foundation.Forms.Engine;
using Harborline.Api.Foundation.Forms.Engine.Capabilities;
using Harborline.Api.Foundation.Governance.Definitions;
using Harborline.Api.Foundation.Assets.Audit;
using Harborline.Api.Foundation.SecurityPolicy.Retention;
using Harborline.Api.Foundation.SecurityPolicy.Models;
using Harborline.Api.Foundation.Governance.Enforcement;
using Harborline.Api.Foundation.Governance.Policy;
using Harborline.Api.Foundation.Governance.Resolution;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Admission;
using Harborline.Api.Foundation.CapabilityAdmission.Authorization;
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
    [Theory]
    [InlineData("platform.binding.catalogue-read", "bafkreift42gowbpcxbfygt36n7tah63ujbb7ekxvzr2deasxigqgp3cb3q")]
    [InlineData("platform.binding.records-read", "bafkreib6tp3widcpawwq43y3kvzqk2hqgxgtabsqxnskemfstgkjlww3xy")]
    public void Released_platform_binding_uses_canonical_sealed_role_and_pinned_content(string key, string expectedCid)
    {
        var request = PlatformPackPreloadHostedService.ReadExportRequest("fixture-author");
        var source = Assert.Single(request.Contents, item => item.Key == key);
        Assert.Equal("1.1.0", source.Version);
        Assert.Equal(RoleReference.Administrator.ToString(), source.Content["offeredRoles"]![0]!.GetValue<string>());
        var canonical = new PackContentCanonicalizer().Canonicalize(source);
        Assert.Equal(expectedCid, canonical.ContentAddress.ToString());
    }

    [Fact]
    public async Task Released_platform_seed_installs_projects_and_audits_pack_author_without_expanding_surfaces()
    {
        using var keys = KeyPair.Generate();
        var codec = new PackFileCodec();
        var request = PlatformPackPreloadHostedService.ReadExportRequest(keys.PrincipalId.ToBase64Url());
        Assert.Equal("1.2.0", request.Version);
        Assert.Equal(PlatformPackPreloadHostedService.PackVersion, request.Version);
        var source = Assert.Single(request.Contents, item => item.Kind == PackContentKind.CascadeDefaults);
        Assert.Equal("platform.defaults.pack-author", source.Key);
        Assert.Equal("1.0.0", source.Version);
        var canonical = new PackContentCanonicalizer().Canonicalize(source);
        Assert.Equal("bafkreiczgrb2t4g3jtgc5cdxtjn4247wyqlw5h7r47bsggwp5bkpbgqjia", canonical.ContentAddress.ToString());
        Assert.True(PackCascadeDefaultsContent.TryParse(Encoding.UTF8.GetString(canonical.CanonicalBytes.Span), out var parsed, out _, out _));
        var declaration = Assert.Single(parsed!.Defaults);
        Assert.Equal("platform.pack.author", declaration.RecordType);
        Assert.Null(declaration.Field);
        Assert.True(declaration.Values.TrackChanges);
        Assert.Equal(39, request.Contents.Count(item => item.Kind == PackContentKind.ViewDefinition));
        Assert.Single(request.Contents, item => item.Kind == PackContentKind.NavWorkspaceConfig);

        var store = new InMemoryPackInstallStore();
        var defaults = new ActiveCascadeDefaultsProjection();
        var installer = new PackInstaller(new PackVerifier(new Ed25519Verifier(), codec), store,
            new PackWorkflowAdmissionAdapter(new WorkflowAdmissionValidator(), defaults: defaults),
            new InMemoryPackInstallAudit(), TestAuthorization.AllowGate());
        var context = new PackInstallContext(Tenant,
            new InMemoryPackTrustStore([new PackTrustRoot(TrustScope.OwnRoster, keys.PrincipalId, request.Epoch, TrustRootStatus.Current)]),
            PackRevocationList.Empty, TimeProvider.System.GetUtcNow(), TimeSpan.FromDays(30), Principal: "test-operator");
        var exporter = new PackExporter(new PackContentCanonicalizer(), new PackDcpCanonicalizer(),
            new PackValidator(new PackContentPiiScanner()), new DcpValidator(DcpCounselRegister.FromEmbeddedResource()), codec, timeProvider: TimeProvider.System);
        var exported = await exporter.ExportAsync(request, new Ed25519Signer(keys));
        Assert.True(exported.Succeeded, string.Join(";", exported.Validation.Errors.Select(error => error.Message)));
        var installed = installer.Install(exported.FileBytes!, context);
        Assert.True(installed.Installed, string.Join(";", installed.RefusalCodes));
        Assert.Equal(canonical.ContentAddress, Assert.Single(store.GetVersion(Tenant, request.Key, request.Version)!.SeedItems,
            item => item.Kind == PackContentKind.CascadeDefaults).ContentAddress);
        Assert.True(installer.Activate(context, request.Key, request.Version).Activated);

        var roles = new InMemoryRoleVocabulary(AccessGrantAuthorizationSeed.RoleDefinitions);
        var (grants, configuration) = TestInMemoryAuthorizationStores.Pair();
        var writer = new AuthorizationDefinitionWriter(configuration, configuration, new AuthorizationDefinitionAdmission(roles),
            new AuthorizationCapabilityBindingAdmission(), TestAuthorization.AllowGate(), grants);
        await new AccessGrantAuthorizationSeed(writer, configuration, grants).InstallAsync(Tenant, TestAuthorization.At, AuthorizationSeedProfile.Production);
        var services = new ServiceCollection().AddLogging().AddInMemoryAssetTypeSystem();
        services.AddSingleton<Harborline.Api.Foundation.Recovery.TenantKey.ITenantKeyProvider,
            Harborline.Api.Foundation.Recovery.TenantKey.InMemoryTenantKeyProvider>();
        services.AddSingleton<Harborline.Api.Foundation.Recovery.Crypto.IFieldEncryptor,
            Harborline.Api.Foundation.Recovery.Crypto.TenantKeyProviderFieldEncryptor>();
        services.AddTestAuthorizationGate().AddTestNodeForms();
        services.AddSingleton<ICascadeDefaultsProjection>(defaults);
        var audit = Substitute.For<IAuditLog>();
        audit.AppendAsync(Arg.Any<AuditAppend>(), Arg.Any<CancellationToken>()).Returns(new AuditId(1));
        services.AddSingleton(audit);
        using var provider = services.BuildServiceProvider();
        var forms = provider.GetRequiredService<IFormDefinitionStore>();
        var views = new Harborline.Api.Foundation.ViewDefinitions.InMemoryViewDefinitionRegistry(
            Substitute.For<Harborline.Api.Foundation.ViewDefinitions.IViewDefinitionDescriptorRegistry>());
        var logger = new ProjectionLogger();
        var projector = new PackSeedProjector(store, provider.GetRequiredService<IEntityTypeRegistry>(), logger,
            forms: forms, schemas: provider.GetRequiredService<ISchemaRegistry>(), time: TimeProvider.System,
            authorizedForms: provider.GetRequiredService<AuthorizedFormDefinitionLifecycle>(), roleVocabulary: roles,
            authorizationDefinitions: writer, defaults: defaults, viewDefinitions: views, renderPlans: new InMemoryRenderPlanCatalogue());
        var summary = await projector.ProjectActivePacksAsync(Tenant);
        Assert.True(summary.Refusals.Count == 0, string.Join(";", logger.Errors));
        var bindings = await configuration.ListAsync(Tenant);
        var platformBindings = bindings.Where(binding => binding.Definition.PublisherPackageId == request.Key).ToArray();
        Assert.Equal(2, platformBindings.Length);
        Assert.All(platformBindings, binding => Assert.Equal(RoleBindingSet.Of(RoleReference.Administrator), binding.EffectiveRoles));
        var auditor = Assert.Single(bindings, binding => binding.EffectiveRoles.Roles.Contains(RoleReference.Auditor));
        Assert.Equal(AccessGrantAuthorizationSeed.PackageId, auditor.Definition.PublisherPackageId);
        Assert.Equal(Permission.AuditRead, auditor.Definition.Operation.Value);
        var catalogue = new CatalogueRegistries(store, defaults: defaults);
        Assert.True(catalogue.IsAvailable(PackContentKind.CascadeDefaults));
        var entry = Assert.Single(await catalogue.ReadAsync(Tenant, PackContentKind.CascadeDefaults));
        Assert.Equal(source.Key, entry.Id);
        Assert.Equal(source.Version, entry.Version);
        Assert.Equal(new CatalogueProvenance(request.Key, request.Version, "pack"), entry.Provenance);
        var resolved = defaults.Resolve(Tenant, request.Key, "platform.pack.author", "packJson");
        Assert.True(resolved.Values.TrackChanges);
        Assert.Equal(source.Key, Assert.Single(resolved.Sources).Value.ContentKey);
        Assert.Null(defaults.Resolve(Tenant, request.Key, "unrelated", "packJson").Values.TrackChanges);
        Assert.Empty(await catalogue.ReadAsync(new("another-tenant"), PackContentKind.CascadeDefaults));
        var form = await forms.GetAsync(new(Tenant, "platform.pack.author", "1.0.0"));
        var policy = provider.GetRequiredService<IAspectResolver>().ResolvePolicy(form, "packJson");
        Assert.NotNull(policy.Effect(EffectKind.Audit, Trigger.Store));
        await provider.GetRequiredService<IFieldPolicyEnforcer>().StoreAsync(new(policy, Encoding.UTF8.GetBytes("{}"),
            Tenant, new("writer"), new("harborline", "node", "record"), TestAuthorization.At, "US"));
        await audit.Received().AppendAsync(Arg.Is<AuditAppend>(entry => entry.Justification == "spine-2 store"), Arg.Any<CancellationToken>());
    }

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
    [InlineData("\"regime\":\"GDPR\"", "\"regime\":\"unknown\"", "pack.defaults.malformed")]
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Whole_pack_refusals_publish_no_defaults(bool smugglesGrant)
    {
        using var fixture = new Fixture();
        var content = smugglesGrant ? """{"entries":[{"subject":"alice","role":"tax.roles/default-reader","scope":"/"}]}"""
            : CatalogueFieldSourceContractTests.Content().ToJsonString();
        fixture.Install("1.0.0", Body, extra: new("bad", smugglesGrant ? PackContentKind.ViewDefinition : PackContentKind.FormDefinition,
            "1.0.0", content, Cid.FromBytes(Encoding.UTF8.GetBytes(content))));
        fixture.Packs.Activate(Tenant, Package, "1.0.0");
        Assert.NotEmpty((await fixture.Projector.ProjectActivePacksAsync(Tenant)).Refusals);
        Assert.Empty(fixture.Defaults.List(Tenant));
        Assert.Empty(await new CatalogueRegistries(fixture.Packs, defaults: fixture.Defaults).ReadAsync(Tenant, PackContentKind.CascadeDefaults));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Narrowing_cannot_remove_effective_publisher_restrictions(int removed)
    {
        const string restrictions = """
            {"schemaVersion":1,"title":"Restrictions","defaults":[
              {"masking":{"revealLast":4},"trackChanges":true},
              {"recordType":"contact","masking":{"revealLast":2}},
              {"recordType":"contact","field":"title","masking":{"revealLast":0}}]}
            """;
        using var fixture = new Fixture();
        fixture.Install("1.0.0", restrictions);
        fixture.Packs.Activate(Tenant, Package, "1.0.0");
        var declarations = JsonNode.Parse(restrictions)!["defaults"]!.DeepClone().AsArray();
        if (removed < 0) declarations.Clear(); else declarations.RemoveAt(removed);
        var installer = new PackInstaller(Substitute.For<IPackVerifier>(), fixture.Packs,
            new PackWorkflowAdmissionAdapter(Substitute.For<IWorkflowAdmissionValidator>(), defaults: fixture.Defaults),
            new InMemoryPackInstallAudit(), TestAuthorization.AllowGate());
        var context = new PackInstallContext(Tenant, new InMemoryPackTrustStore([]), PackRevocationList.Empty,
            TestAuthorization.At, TimeSpan.FromDays(30), Principal: "test-operator");
        var before = Assert.Single(fixture.Packs.ListInstalled(Tenant));
        var refusal = installer.Narrow(context, Package, "defaults", new JsonObject { ["defaults"] = declarations },
            TestAuthorization.AllowedDecision(Tenant, Package, "pack", Permission.PackagesOperate));
        Assert.False(refusal.Recorded);
        Assert.Equal("pack.defaults.relax_forbidden", refusal.RefusalCode);
        Assert.Empty(fixture.Packs.GetOverrides(Tenant, Package));
        Assert.Equal(before, Assert.Single(fixture.Packs.ListInstalled(Tenant)));
    }

    [Theory]
    [InlineData("\"classification\":[{\"system\":\"harborline/data-classification\",\"code\":\"pii\"}]")]
    [InlineData("\"personalData\":true")]
    [InlineData("\"masking\":{\"revealLast\":0}")]
    [InlineData("\"retention\":{\"regime\":\"GDPR\",\"floorClass\":\"Identity\",\"minimumRetentionDays\":30}")]
    [InlineData("\"conflictPolicy\":\"ask\"")]
    [InlineData("\"trackChanges\":true")]
    public void Composed_admission_preserves_each_declared_axis(string axis)
    {
        var seed = "{\"schemaVersion\":1,\"title\":\"Publisher policy\",\"defaults\":[{\"recordType\":\"contact\"," + axis + "}]}";
        var composed = """{"schemaVersion":1,"title":"Publisher policy","defaults":[]}""";
        var adapter = new PackWorkflowAdmissionAdapter(Substitute.For<IWorkflowAdmissionValidator>(), defaults: new());
        var refusal = Assert.Single(adapter.Admit([new(Package, "defaults", PackContentKind.CascadeDefaults,
            "1.0.0", composed, SeedCanonicalJson: seed)], Tenant).Refusals);
        Assert.Equal(CascadeDefaultsRestrictionCheck.Refused, refusal.Code);
    }

    [Fact]
    public async Task Legacy_weakening_override_is_refused_again_at_projection()
    {
        using var fixture = new Fixture();
        fixture.Install("1.0.0", Body);
        fixture.Packs.Activate(Tenant, Package, "1.0.0");
        await fixture.Projector.ProjectActivePacksAsync(Tenant);
        Assert.Single(fixture.Defaults.List(Tenant));
        fixture.Packs.SaveOverride(Tenant, Package, new("defaults", JsonNode.Parse("""{"defaults":[]}""")!));
        var refusal = Assert.Single((await fixture.Projector.ProjectActivePacksAsync(Tenant)).Refusals);
        Assert.Equal(CascadeDefaultsRestrictionCheck.Refused, refusal.Code);
        Assert.Empty(fixture.Defaults.List(Tenant));
    }

    [Fact]
    public async Task Narrowing_resolves_inherited_restrictions_across_defaults_items()
    {
        using var fixture = new Fixture();
        const string package = """{"schemaVersion":1,"title":"Package","defaults":[{"masking":{"revealLast":0}}]}""";
        const string field = """{"schemaVersion":1,"title":"Field","defaults":[{"recordType":"contact","field":"title","masking":{"revealLast":4}}]}""";
        fixture.Install("1.0.0", package, extra: new("field-defaults", PackContentKind.CascadeDefaults, "1.0.0", field, Cid.FromBytes(Encoding.UTF8.GetBytes(field))));
        fixture.Packs.Activate(Tenant, Package, "1.0.0");
        var installer = new PackInstaller(Substitute.For<IPackVerifier>(), fixture.Packs,
            new PackWorkflowAdmissionAdapter(Substitute.For<IWorkflowAdmissionValidator>(), defaults: fixture.Defaults),
            new InMemoryPackInstallAudit(), TestAuthorization.AllowGate());
        var context = new PackInstallContext(Tenant, new InMemoryPackTrustStore([]), PackRevocationList.Empty,
            TestAuthorization.At, TimeSpan.FromDays(30), Principal: "test-operator");
        Assert.True(installer.Narrow(context, Package, "field-defaults", JsonNode.Parse("""{"defaults":[]}""")!,
            TestAuthorization.AllowedDecision(Tenant, Package, "pack", Permission.PackagesOperate)).Recorded);
        Assert.Empty((await fixture.Projector.ProjectActivePacksAsync(Tenant)).Refusals);
        Assert.Equal(0, fixture.Defaults.Resolve(Tenant, Package, "contact", "title").Values.Masking!.RevealLast);
    }

    [Fact]
    public async Task FormEngine_enforces_unclassified_masking_and_store_audit_defaults()
    {
        using var fixture = new Fixture();
        var body = Body.Replace("\"trackChanges\":false", "\"trackChanges\":true", StringComparison.Ordinal);
        fixture.Install("1.0.0", body, includeForm: true);
        fixture.Packs.Activate(Tenant, Package, "1.0.0");
        Assert.Empty((await fixture.Projector.ProjectActivePacksAsync(Tenant)).Refusals);
        var services = new ServiceCollection();
        services.AddSingleton<Harborline.Api.Foundation.Recovery.TenantKey.ITenantKeyProvider,
            Harborline.Api.Foundation.Recovery.TenantKey.InMemoryTenantKeyProvider>();
        services.AddSingleton<Harborline.Api.Foundation.Recovery.Crypto.IFieldEncryptor,
            Harborline.Api.Foundation.Recovery.Crypto.TenantKeyProviderFieldEncryptor>();
        services.AddTestAuthorizationGate().AddTestNodeForms();
        services.AddSingleton<IFormDefinitionStore>(fixture.Forms);
        services.AddSingleton<ISchemaRegistry>(fixture.Schemas);
        services.AddSingleton<ICascadeDefaultsProjection>(fixture.Defaults);
        var audit = Substitute.For<IAuditLog>();
        audit.AppendAsync(Arg.Any<AuditAppend>(), Arg.Any<CancellationToken>()).Returns(new AuditId(1));
        services.AddSingleton(audit);
        using var provider = services.BuildServiceProvider();
        var now = TimeProvider.System.GetUtcNow();
        var bearer = await provider.GetRequiredService<IFormCapabilityIssuer>().IssueAsync(Tenant, new("reader"),
            ["tax.roles/default-reader"], [FormCapabilityAction.Read, FormCapabilityAction.Write], now.AddHours(1));
        var token = await provider.GetRequiredService<IFormCapabilityVerifier>().VerifyAsync(bearer, now);
        var engine = provider.GetRequiredService<IFormEngine>();
        using var candidate = JsonDocument.Parse("""{"title":"12345678"}""");
        var receipt = await engine.SaveWithReceiptAsync(new("contact"), candidate, token,
            TestAuthorization.FormWrite(token, new("contact"), now), CancellationToken.None);
        var view = await engine.RenderAsync(new("contact"), receipt.InstanceId, token, CancellationToken.None);
        var title = view.Sections.SelectMany(section => section.Fields).Single(field => field.Name == "title");
        Assert.True(title.IsReadable);
        Assert.Equal("****5678", title.Value!.Value.GetString());
        await audit.Received().AppendAsync(Arg.Is<AuditAppend>(entry => entry.Justification == "spine-2 store"), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("GDPR", "Identity", 45, 45)]
    [InlineData("GDPR", "Identity", 5, 10)]
    [InlineData("HIPAA", "Identity", 30, 2192)]
    [InlineData("PCI_DSS_v4", "Financial", 30, 366)]
    public async Task Declared_retention_and_regime_raise_store_and_definition_verdicts(string regime, string floorClass, int days, int expectedDays)
    {
        using var fixture = new Fixture();
        var body = Body.Replace("\"GDPR\"", JsonSerializer.Serialize(regime), StringComparison.Ordinal)
            .Replace("\"Identity\"", JsonSerializer.Serialize(floorClass), StringComparison.Ordinal)
            .Replace("\"minimumRetentionDays\":30", $"\"minimumRetentionDays\":{days}", StringComparison.Ordinal);
        fixture.Install("1.0.0", body, includeForm: true);
        fixture.Packs.Activate(Tenant, Package, "1.0.0");
        await fixture.Projector.ProjectActivePacksAsync(Tenant);
        var created = TestAuthorization.At;
        var retention = Substitute.For<IRetentionPolicyResolver>();
        retention.ResolveAsync(Tenant, Arg.Any<AuditEventClass>(), created, Arg.Any<CancellationToken>())
            .Returns(call => new RetentionVerdict(call.Arg<AuditEventClass>(), created.AddDays(10), created.AddDays(20), false));
        var services = new ServiceCollection();
        services.AddSingleton<Harborline.Api.Foundation.Recovery.TenantKey.ITenantKeyProvider,
            Harborline.Api.Foundation.Recovery.TenantKey.InMemoryTenantKeyProvider>();
        services.AddSingleton<Harborline.Api.Foundation.Recovery.Crypto.IFieldEncryptor,
            Harborline.Api.Foundation.Recovery.Crypto.TenantKeyProviderFieldEncryptor>();
        services.AddTestAuthorizationGate().AddTestNodeForms();
        services.AddSingleton<ICascadeDefaultsProjection>(fixture.Defaults);
        services.AddSingleton(retention);
        using var provider = services.BuildServiceProvider();
        var form = await fixture.Forms.GetAsync(new(Tenant, "contact", "1.0.0"));
        var policy = provider.GetRequiredService<IAspectResolver>().ResolvePolicy(form, "title");
        var stored = await provider.GetRequiredService<IFieldPolicyEnforcer>().StoreAsync(new(policy, Encoding.UTF8.GetBytes("value"),
            Tenant, new("writer"), new("harborline", "node", "record"), created, "US"));
        Assert.Equal(created.AddDays(expectedDays), stored.Retention!.MinimumHoldUntil);
        Assert.True(stored.Retention.MaximumHoldUntil >= stored.Retention.MinimumHoldUntil);
        var envelope = await provider.GetRequiredService<IFormDefinitionEnvelopeResolver>().ResolveAsync(form, created);
        Assert.Equal(created.AddDays(expectedDays), envelope.RetentionClass.MinimumHoldUntil);
    }

    private sealed class ProjectionLogger : Microsoft.Extensions.Logging.ILogger<PackSeedProjector>
    {
        public List<string> Errors { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level, Microsoft.Extensions.Logging.EventId id,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (exception is not null) Errors.Add(exception.ToString());
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly ServiceProvider services = new ServiceCollection().AddLogging().AddInMemoryAssetTypeSystem().BuildServiceProvider();
        public readonly InMemoryPackInstallStore Packs = new();
        public readonly ActiveCascadeDefaultsProjection Defaults = new();
        public readonly InMemoryFormDefinitionStore Forms = new(TimeProvider.System);
        public readonly InMemorySchemaRegistry Schemas = new(TimeProvider.System);
        public PackSeedProjector Projector { get; }
        public Fixture() => Projector = new(Packs, services.GetRequiredService<IEntityTypeRegistry>(),
            NullLogger<PackSeedProjector>.Instance, time: TimeProvider.System, defaults: Defaults,
            forms: Forms, schemas: Schemas,
            authorizedForms: TestAuthorization.FormLifecycle(Forms, TestAuthorization.AllowGate(), new RoleGateAdmission(
                new InMemoryRoleVocabulary([RoleDefinition.CreateTenantRole(RoleDefinitionId.New(), "default-reader", "Reader", Tenant)]))));
        public void Install(string version, string body, bool includeForm = false, string package = Package, PackSeedItem? extra = null)
        {
            var items = new List<PackSeedItem> { new("defaults", PackContentKind.CascadeDefaults, version, body, Cid.FromBytes(Encoding.UTF8.GetBytes(body))) };
            if (extra is not null) items.Add(extra);
            if (includeForm)
            {
                var content = CatalogueFieldSourceContractTests.Content(false);
                content["overlay"]!["sections"]![0]!["access"] = JsonNode.Parse("""{"readRoles":["tax.roles/default-reader"],"writeRoles":["tax.roles/default-reader"]}""");
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
