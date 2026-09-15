using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Blocks.Assets.Registry.DependencyInjection;
using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.Packs.Dcp;
using Harborline.Api.Foundation.Packs.Export;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Admission;
using Harborline.Api.Foundation.Packs.Install.Audit;
using Harborline.Api.Foundation.Packs.Install.Compatibility;
using Harborline.Api.Foundation.Packs.Install.Trust;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Serialization;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Foundation.Packs.Validation;
using Harborline.Api.Foundation.Packs.Verify;
using Harborline.Api.Kernel.Schema;
using Harborline.Api.LocalNodeHost.Data.PackProjection;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Authorization;

using NSubstitute;
using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Forms;

public sealed class CatalogueFieldSourceInstallTests
{
    private static readonly TenantId Tenant = new("catalogue-admission-tests");
    private const string PackKey = "tests.catalogue-details";

    [Theory]
    [InlineData("missing-requirement", CatalogueFieldSourceCodes.MissingSupportDeclaration)]
    [InlineData("missing-version", CatalogueFieldSourceCodes.MissingSupportDeclaration)]
    [InlineData("malformed", CatalogueFieldSourceCodes.MalformedSourceMapping)]
    [InlineData("unknown", CatalogueFieldSourceCodes.UnknownSourceMapping)]
    [InlineData("unsupported-version", CatalogueFieldSourceCodes.UnsupportedCapabilityOrSchemaVersion)]
    [InlineData("unsupported-mapping", CatalogueFieldSourceCodes.UnsupportedSourceMapping)]
    [InlineData("unwired", CatalogueFieldSourceCodes.UnsupportedCapabilityOrSchemaVersion)]
    public async Task Rejected_upgrade_preserves_installed_seed_and_watermark(string mutation, string code)
    {
        using var keys = KeyPair.Generate();
        var codec = new PackFileCodec();
        var store = new InMemoryPackInstallStore();
        var admission = new CatalogueFieldSourceAdmission(_ => mutation != "unwired");
        var installer = Installer(keys, codec, store, admission);
        var context = Context(keys);
        var legacy = await Export(keys, codec, CatalogueFieldSourceContractTests.Content(false), "1.0.0", []);
        Assert.True(installer.Install(legacy, context).Installed);
        var before = Assert.Single(store.ListInstalled(Tenant));
        var watermark = store.GetWatermark(Tenant, PackKey);
        var content = CatalogueFieldSourceContractTests.Content();
        switch (mutation)
        {
            case "missing-version": content["catalogueFieldSource"]!.AsObject().Remove("coordinateSchemaVersion"); break;
            case "malformed": content["catalogueFieldSource"]!["fields"]![0]!["extra"] = true; break;
            case "unknown": content["catalogueFieldSource"]!["fields"]![0]!["source"] = "catalogue.entry.secret"; break;
            case "unsupported-version": content["catalogueFieldSource"]!["sourceMappingSchemaVersion"] = 2; break;
            case "unsupported-mapping": content["catalogueFieldSource"]!["sourceKind"] = "ViewDefinition"; break;
        }
        var bytes = await Export(keys, codec, content, "2.0.0",
            mutation == "missing-requirement" ? [] : [CatalogueFieldSourceContract.CapabilityId]);
        var outcome = installer.Install(bytes, context);
        Assert.False(outcome.Installed);
        Assert.Equal(code, Assert.Single(outcome.Preview.AdmissionRefusals).Code);
        Assert.Equal(before, Assert.Single(store.ListInstalled(Tenant)));
        Assert.Equal(watermark, store.GetWatermark(Tenant, PackKey));
        Assert.Empty(store.GetOverrides(Tenant, PackKey));
    }

    [Fact]
    public async Task Admitted_profile_projects_and_replays_without_losing_mapping_or_provenance()
    {
        using var keys = KeyPair.Generate();
        var codec = new PackFileCodec();
        var store = new InMemoryPackInstallStore();
        // A test runtime positive control proves transport; the production host does not advertise this profile yet.
        var admission = new CatalogueFieldSourceAdmission(_ => true);
        var installer = Installer(keys, codec, store, admission);
        var context = Context(keys);
        var bytes = await Export(keys, codec, CatalogueFieldSourceContractTests.Content(), "1.0.0", [CatalogueFieldSourceContract.CapabilityId]);
        Assert.True(installer.Install(bytes, context).Installed);
        Assert.True(installer.Activate(Tenant, PackKey, "1.0.0", context.Now, "test-operator").Activated);
        using var services = new ServiceCollection().AddLogging().AddInMemoryAssetTypeSystem().BuildServiceProvider();
        using var forms = new InMemoryFormDefinitionStore(TimeProvider.System);
        var schemas = new InMemorySchemaRegistry(TimeProvider.System);
        var projector = new PackSeedProjector(store, services.GetRequiredService<IEntityTypeRegistry>(),
            NullLogger<PackSeedProjector>.Instance, forms: forms, schemas: schemas, time: TimeProvider.System,
            authorizedForms: TestAuthorization.FormLifecycle(forms, TestAuthorization.AllowGate(), TestAuthorization.RoleGate()),
            catalogueFields: admission);
        Assert.Equal(1, (await projector.ProjectActivePacksAsync(Tenant)).FormDefinitionsPublished);
        var definition = await forms.GetAsync(new DefinitionCoordinates(Tenant, "platform.detail.form", "1.0.0"));
        Assert.Equal(CatalogueFieldSourceContract.Fields, definition.CatalogueFieldSource!.Fields);
        Assert.Equal(CascadeLayer.Pack, definition.Envelope.CascadeLayer);
        Assert.Equal(PackKey, definition.PackSource!.PackId);
        Assert.Equal(1, (await projector.ProjectActivePacksAsync(Tenant)).FormDefinitionsAlreadyPresent);

        var unwiredProjector = new PackSeedProjector(store, services.GetRequiredService<IEntityTypeRegistry>(),
            NullLogger<PackSeedProjector>.Instance, time: TimeProvider.System);
        var refusal = Assert.Single((await unwiredProjector.ProjectActivePacksAsync(Tenant)).Refusals);
        Assert.Equal(CatalogueFieldSourceCodes.UnsupportedCapabilityOrSchemaVersion, refusal.Code);
        Assert.Equal(definition, await forms.GetAsync(new DefinitionCoordinates(Tenant, "platform.detail.form", "1.0.0")));
    }

    [Fact]
    public async Task Foundation_default_refuses_opt_in_when_host_adapter_is_absent()
    {
        using var keys = KeyPair.Generate();
        var codec = new PackFileCodec();
        var store = new InMemoryPackInstallStore();
        var installer = new PackInstaller(new PackVerifier(new Ed25519Verifier(), codec), store,
            new WorkflowRefusingPackContentAdmission(), new InMemoryPackInstallAudit(), TestAuthorization.AllowGate(), Platform());
        var bytes = await Export(keys, codec, CatalogueFieldSourceContractTests.Content(), "1.0.0", [CatalogueFieldSourceContract.CapabilityId]);
        var outcome = installer.Install(bytes, Context(keys));
        Assert.False(outcome.Installed);
        Assert.Equal(PackAdmissionCodes.NotWired, Assert.Single(outcome.Preview.AdmissionRefusals).Code);
        Assert.Empty(store.ListInstalled(Tenant));
    }

    private static PackInstaller Installer(KeyPair keys, PackFileCodec codec, InMemoryPackInstallStore store, CatalogueFieldSourceAdmission admission)
        => new(new PackVerifier(new Ed25519Verifier(), codec), store,
            new PackWorkflowAdmissionAdapter(Substitute.For<IWorkflowAdmissionValidator>(), catalogueFields: admission),
            new InMemoryPackInstallAudit(), TestAuthorization.AllowGate(), Platform());

    private static PackPlatformCompatibility Platform() => new("1.0.0",
        [new PackProjectorCase(PackContentKind.FormDefinition, [CatalogueFieldSourceContract.CapabilityId])]);

    private static PackInstallContext Context(KeyPair keys) => new(Tenant,
        new InMemoryPackTrustStore([new PackTrustRoot(TrustScope.OwnRoster, keys.PrincipalId, 1, TrustRootStatus.Current)]),
        PackRevocationList.Empty, TimeProvider.System.GetUtcNow(), TimeSpan.FromDays(30), Principal: "test-operator");

    private static async Task<byte[]> Export(KeyPair keys, PackFileCodec codec, JsonNode content, string version, IReadOnlyList<string> requirements)
    {
        var exporter = new PackExporter(new PackContentCanonicalizer(), new PackDcpCanonicalizer(),
            new PackValidator(new PackContentPiiScanner()), new DcpValidator(DcpCounselRegister.FromEmbeddedResource()),
            codec, timeProvider: TimeProvider.System);
        var exported = await exporter.ExportAsync(new PackExportRequest(PackKey, version, "Catalogue admission tests",
            "Strict transport admission fixture.", PackScopeTier.Vertical,
            [new PackContentSource("platform.detail.form", PackContentKind.FormDefinition, version, content)], [], requirements,
            Epoch: 1, Dcp: DomainComplianceProfile.General("test-author")), new Ed25519Signer(keys));
        Assert.True(exported.Succeeded, string.Join(";", exported.Validation.Errors.Select(e => e.Message)));
        return exported.FileBytes!;
    }
}
