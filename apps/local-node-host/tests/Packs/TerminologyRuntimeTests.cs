using System.Text.Json;
using System.Text.Json.Nodes;
using Harborline.Api.Blocks.Assets.Registry.DependencyInjection;
using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Catalog.Templates;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.Packs.Dcp;
using Harborline.Api.Foundation.Packs.Export;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Admission;
using Harborline.Api.Foundation.Packs.Install.Audit;
using Harborline.Api.Foundation.Packs.Install.Trust;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Serialization;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Foundation.Packs.Validation;
using Harborline.Api.Foundation.Packs.Verify;
using Harborline.Api.LocalNodeHost.Data.PackProjection;
using Harborline.Api.LocalNodeHost.Health;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

public sealed class TerminologyRuntimeTests
{
    private static readonly TenantId Tenant = new("aaaaaaaa-0000-0000-0000-000000000233");
    private static readonly TenantId OtherTenant = new("bbbbbbbb-0000-0000-0000-000000000233");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static JsonObject Fixture() => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Terminology", "v1.json")))!.AsObject();
    private static PackComposedItem Item(JsonNode body, string? seed = null) => new("terminology.test", "operations.asset",
        PackContentKind.TerminologyOverride, "1.0.0", body.ToJsonString(), SeedCanonicalJson: seed);

    [Fact]
    public void Fixture_binds_stable_identity_scope_fallback_and_stale_response()
    {
        var fixture = Fixture();
        var seed = fixture["content"]!;
        var composed = TemplateMerger.ApplyMergePatch(seed, fixture["overridePatch"]!)!;
        var request = fixture["request"]!;
        Assert.Equal(1, request["schemaVersion"]!.GetValue<int>());
        Assert.Equal(Tenant.ToString(), request["tenantId"]!.GetValue<string>());
        Assert.Null(PackTerminologyContent.TryRead(Item(composed, seed.ToJsonString()), Tenant, out var content));
        var result = PackTerminologyContent.Resolve(content!, request["userLocale"]!.GetValue<string>());
        Assert.Equal(request["stableId"]!.GetValue<string>(), result.StableId);
        Assert.True(JsonNode.DeepEquals(fixture["response"], JsonSerializer.SerializeToNode(result, Json)));
        Assert.Equal("Asset", PackTerminologyContent.Resolve(content!, "es-MX").Text);
        Assert.Equal("en", PackTerminologyContent.Resolve(content!, "es-MX").ResolvedLocale);
        Assert.Equal("Anlage", PackTerminologyContent.Resolve(content!, "de").Text);
        Assert.True(PackTerminologyContent.Resolve(content!, "de").IsStale);
        Assert.Throws<ArgumentException>(() => PackTerminologyContent.Resolve(content!, string.Empty));
    }

    [Theory]
    [InlineData("schemaVersion", "2", PackTerminologyContent.UnsupportedVersion)]
    [InlineData("tenantId", "\"another-tenant\"", PackTerminologyContent.CrossTenant)]
    [InlineData("stableId", "\"label-is-not-identity\"", PackTerminologyContent.Malformed)]
    [InlineData("version", "\"2.0.0\"", PackTerminologyContent.Malformed)]
    [InlineData("unknown", "true", PackTerminologyContent.Malformed)]
    [InlineData("packageTranslations", "[]", PackTerminologyContent.Malformed)]
    [InlineData("tenantOverrides", "{\"fr\":{\"text\":\"Bonjour\"}}", PackTerminologyContent.Malformed)]
    [InlineData("defaultLocale", "null", PackTerminologyContent.Malformed)]
    public void Admission_refuses_invalid_composed_content_without_model(string field, string value, string code)
    {
        var body = Fixture()["content"]!;
        body[field] = JsonNode.Parse(value);
        var refusal = Assert.Single(PackTerminologyContent.Validate([Item(body)], Tenant));
        Assert.Equal(code, refusal.Code);
        Assert.Equal(code, PackTerminologyContent.TryRead(Item(body), Tenant, out var content));
        Assert.Null(content);
    }

    [Fact]
    public void Unknown_duplicate_missing_and_cross_tenant_source_fields_fail_closed()
    {
        var body = Fixture()["content"]!;
        var json = body.ToJsonString();
        var item = Item(body) with { CanonicalJson = json.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1", StringComparison.Ordinal) };
        Assert.Equal(PackTerminologyContent.Malformed, PackTerminologyContent.TryRead(item, Tenant, out _));
        body.AsObject().Remove("schemaVersion");
        Assert.Equal(PackTerminologyContent.Malformed, PackTerminologyContent.TryRead(Item(body), Tenant, out _));
        var scoped = Fixture()["content"]!;
        scoped["tenantId"] = OtherTenant.ToString();
        Assert.Equal(PackTerminologyContent.CrossTenant, PackTerminologyContent.TryRead(Item(Fixture()["content"]!, scoped.ToJsonString()), Tenant, out _));
        Assert.False(new WorkflowRefusingPackContentAdmission().Admit([Item(Fixture()["content"]!)], Tenant).IsAdmissible);
        Assert.False(new PackWorkflowAdmissionAdapter(new WorkflowAdmissionValidator()).Admit([Item(Fixture()["content"]!)], Tenant).IsAdmissible);
    }

    [Fact]
    public void Tenant_patch_cannot_replace_package_translation_or_clear_unsupported_source()
    {
        var seed = Fixture()["content"]!;
        var changed = seed.DeepClone();
        changed["packageTranslations"]!["en"]!["text"] = "Forged package value";
        Assert.Equal(PackTerminologyContent.ImmutableSourceChanged, PackTerminologyContent.TryRead(Item(changed, seed.ToJsonString()), Tenant, out _));
        seed["schemaVersion"] = 99;
        Assert.Equal(PackTerminologyContent.UnsupportedVersion, PackTerminologyContent.TryRead(Item(Fixture()["content"]!, seed.ToJsonString()), Tenant, out _));
        var forgedTenant = Fixture()["content"]!;
        forgedTenant["tenantOverrides"] = Fixture()["overridePatch"]!["tenantOverrides"]!.DeepClone();
        Assert.Equal(PackTerminologyContent.Malformed, PackTerminologyContent.TryRead(Item(forgedTenant, forgedTenant.ToJsonString()), Tenant, out _));
    }

    [Fact]
    public async Task Signed_install_active_composition_catalogue_and_deactivation_use_one_tenant_projection()
    {
        using var fixture = new Runtime();
        var content = Fixture()["content"]!;
        await fixture.Install(content);
        Assert.Empty(fixture.Projection.List(Tenant)); // Draft installation is not runtime admission.
        Assert.True(fixture.Installer!.Activate(Tenant, "terminology.test", "1.0.0", TestAuthorization.At, "operator").Activated);
        Assert.Empty((await fixture.Projector.ProjectActivePacksAsync(Tenant)).Refusals);
        Assert.Equal("Actif", fixture.Projection.Resolve(Tenant, "operations.asset", "fr")!.Text);
        fixture.Packs.SaveOverride(Tenant, "terminology.test", new PackTenantOverride("operations.asset", Fixture()["overridePatch"]!));
        Assert.Empty((await fixture.Projector.ProjectActivePacksAsync(Tenant)).Refusals);
        var resolved = fixture.Projection.Resolve(Tenant, "operations.asset", "fr-CA")!;
        Assert.Equal("Équipement", resolved.Text);
        Assert.True(resolved.IsStale);
        var list = await fixture.Catalogue.ListAsync(Tenant, PackContentKind.TerminologyOverride);
        Assert.Empty(list.KindsUnavailable);
        var row = Assert.Single(list.Entries);
        Assert.Equal("operations.asset", row.Id);
        Assert.Equal("terminology.test", row.Provenance.PackKey);
        Assert.Equal("1.0.0", row.Provenance.PackVersion);
        Assert.Equal("Équipement", row.Body.GetProperty("tenantOverrides").GetProperty("fr").GetProperty("text").GetString());
        Assert.Null(PackTerminologyContent.TryRead(Item(JsonNode.Parse(row.Body.GetRawText())!), Tenant, out _));
        Assert.Null(await fixture.Catalogue.GetAsync(Tenant, PackContentKind.TerminologyOverride, row.Id, "9.0.0"));
        Assert.Null(fixture.Projection.Resolve(OtherTenant, row.Id, "fr"));
        Assert.Empty((await fixture.Catalogue.ListAsync(OtherTenant, PackContentKind.TerminologyOverride)).Entries);
        Assert.True(fixture.Installer!.Deactivate(Tenant, "terminology.test", "1.0.0", TestAuthorization.At, "operator").Deactivated);
        await fixture.Projector.ProjectActivePacksAsync(Tenant);
        Assert.Empty(fixture.Projection.List(Tenant));
        Assert.Null(fixture.Projection.Resolve(Tenant, row.Id, "fr"));
    }

    [Fact]
    public async Task Malformed_overlay_clears_prior_runtime_content_and_refuses_reprojection()
    {
        using var fixture = new Runtime();
        await fixture.Install(Fixture()["content"]!);
        fixture.Installer!.Activate(Tenant, "terminology.test", "1.0.0", TestAuthorization.At, "operator");
        await fixture.Projector.ProjectActivePacksAsync(Tenant);
        fixture.Packs.SaveOverride(Tenant, "terminology.test", new PackTenantOverride("operations.asset", JsonNode.Parse("{\"schemaVersion\":2}")!));
        var result = await fixture.Projector.ProjectActivePacksAsync(Tenant);
        Assert.Contains(result.Refusals, refusal => refusal.Code == PackTerminologyContent.UnsupportedVersion);
        Assert.Empty(fixture.Projection.List(Tenant));
    }

    [Fact]
    public async Task Unsupported_signed_upgrade_preserves_installed_and_admitted_state()
    {
        using var fixture = new Runtime();
        await fixture.Install(Fixture()["content"]!);
        fixture.Installer!.Activate(Tenant, "terminology.test", "1.0.0", TestAuthorization.At, "operator");
        await fixture.Projector.ProjectActivePacksAsync(Tenant);
        var unsupported = Fixture()["content"]!;
        unsupported["schemaVersion"] = 8;
        var result = await fixture.Install(unsupported, "2.0.0", expectInstalled: false);
        Assert.Contains(result.Preview.AdmissionRefusals, refusal => refusal.Code == PackTerminologyContent.UnsupportedVersion);
        Assert.Single(fixture.Packs.ListInstalled(Tenant));
        Assert.Equal("1.0.0", Assert.Single(fixture.Projection.List(Tenant)).Provenance.PackVersion);
        Assert.Null(fixture.Packs.GetVersion(Tenant, "terminology.test", "2.0.0"));
    }

    [Fact]
    public async Task Superseded_and_non_owner_content_is_never_returned()
    {
        using var fixture = new Runtime();
        await fixture.Install(Fixture()["content"]!);
        fixture.Installer!.Activate(Tenant, "terminology.test", "1.0.0", TestAuthorization.At, "operator");
        await fixture.Projector.ProjectActivePacksAsync(Tenant);
        await fixture.Install(Fixture()["content"]!, "2.0.0");
        fixture.Installer!.Activate(Tenant, "terminology.test", "2.0.0", TestAuthorization.At, "operator");
        await fixture.Projector.ProjectActivePacksAsync(Tenant);
        Assert.Equal("2.0.0", Assert.Single(fixture.Projection.List(Tenant)).Provenance.PackVersion);

        // Defensive legacy state: two active claims; only recorded ownership can supply a row.
        var owner = fixture.Packs.GetActive(Tenant, "terminology.test")! with { PackKey = "terminology.owner", Lifecycle = PackLifecycleState.Draft };
        fixture.Packs.Commit(new PackInstallTransaction(Tenant, owner,
            new PackInstallWatermark(owner.PackKey, owner.Version, new Dictionary<string, int>()), []));
        fixture.Packs.Activate(Tenant, owner.PackKey, owner.Version);
        var unresolved = await fixture.Projector.ProjectActivePacksAsync(Tenant);
        Assert.Empty(fixture.Projection.List(Tenant));
        Assert.Contains(unresolved.Refusals, refusal => refusal.Code == "pack.terminology.ownership_unresolved");
        fixture.Packs.RecordKeyOwnership(Tenant, "operations.asset", owner.PackKey);
        await fixture.Projector.ProjectActivePacksAsync(Tenant);
        Assert.Equal(owner.PackKey, Assert.Single(fixture.Projection.List(Tenant)).Provenance.PackKey);
        fixture.Packs.Deactivate(Tenant, owner.PackKey, owner.Version);
        await fixture.Projector.ProjectActivePacksAsync(Tenant);
        Assert.DoesNotContain(fixture.Projection.List(Tenant), row => row.Provenance.PackKey == owner.PackKey);
    }

    [Fact]
    public async Task Available_empty_is_distinct_from_absent_consumer()
    {
        using var forms = new InMemoryFormDefinitionStore(TimeProvider.System);
        var lifecycle = TestAuthorization.FormLifecycle(forms, TestAuthorization.AllowGate(), TestAuthorization.RoleGate());
        var unavailable = await new ProjectedCatalogue(lifecycle).ListAsync(Tenant, PackContentKind.TerminologyOverride);
        Assert.Equal([PackContentKind.TerminologyOverride], unavailable.KindsUnavailable);
        var available = await new ProjectedCatalogue(lifecycle, terminology: new TerminologyProjection()).ListAsync(Tenant, PackContentKind.TerminologyOverride);
        Assert.Empty(available.KindsUnavailable);
        Assert.Empty(available.Entries);
    }

    private sealed class Runtime : IDisposable
    {
        private readonly ServiceProvider services = new ServiceCollection().AddLogging().AddInMemoryAssetTypeSystem().BuildServiceProvider();
        private readonly InMemoryFormDefinitionStore forms = new(TimeProvider.System);
        private readonly KeyPair signing = KeyPair.Generate();
        public InMemoryPackInstallStore Packs { get; } = new();
        public TerminologyProjection Projection { get; } = new();
        public IPackSeedProjector Projector { get; }
        public ProjectedCatalogue Catalogue { get; }
        public PackInstaller? Installer { get; private set; }
        public Runtime()
        {
            Projector = new PackSeedProjector(Packs, services.GetRequiredService<IEntityTypeRegistry>(), NullLogger<PackSeedProjector>.Instance,
                time: TimeProvider.System, terminology: Projection);
            Catalogue = new ProjectedCatalogue(TestAuthorization.FormLifecycle(forms, TestAuthorization.AllowGate(), TestAuthorization.RoleGate()), terminology: Projection);
        }
        public async Task<PackInstallOutcome> Install(JsonNode body, string version = "1.0.0", bool expectInstalled = true)
        {
            var codec = new PackFileCodec();
            var exporter = new PackExporter(new PackContentCanonicalizer(), new PackDcpCanonicalizer(),
                new PackValidator(new PackContentPiiScanner()), new DcpValidator(DcpCounselRegister.FromEmbeddedResource()), codec, timeProvider: TimeProvider.System);
            var export = await exporter.ExportAsync(new PackExportRequest("terminology.test", version, "Terminology test", "Terminology runtime proof",
                PackScopeTier.Horizontal, [new PackContentSource("operations.asset", PackContentKind.TerminologyOverride, "1.0.0", body)],
                [], [], Epoch: 1, Dcp: DomainComplianceProfile.General("test-author")), new Ed25519Signer(signing));
            Assert.True(export.Succeeded, string.Join("; ", export.Validation.Errors.Select(error => error.Message)));
            Installer = new PackInstaller(new PackVerifier(new Ed25519Verifier(), codec), Packs,
                new PackWorkflowAdmissionAdapter(new WorkflowAdmissionValidator(), terminology: Projection), new InMemoryPackInstallAudit(), TestAuthorization.AllowGate());
            var trust = new InMemoryPackTrustStore([new PackTrustRoot(TrustScope.OwnRoster, signing.PrincipalId, 1, TrustRootStatus.Current)]);
            var result = Installer.Install(export.FileBytes!, new PackInstallContext(Tenant, trust, PackRevocationList.Empty,
                TestAuthorization.At, TimeSpan.FromDays(30), Principal: "operator"));
            Assert.True(result.Installed == expectInstalled, string.Join("; ", result.RefusalCodes));
            return result;
        }
        public void Dispose() { forms.Dispose(); services.Dispose(); signing.Dispose(); }
    }
}
