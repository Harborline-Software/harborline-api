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
using Harborline.Api.Foundation.Packs.Install.Trust;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Serialization;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Foundation.Packs.Validation;
using Harborline.Api.Foundation.Packs.Verify;
using Harborline.Api.Kernel.Schema;
using Harborline.Api.LocalNodeHost.Data.Compose;
using Harborline.Api.LocalNodeHost.Data.PackProjection;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Blocks.Assets.Registry.DependencyInjection;
using Harborline.Api.Blocks.Assets.Registry.Services;

namespace Harborline.Api.LocalNodeHost.Tests.Definitions;

/// <summary>Exercises the definition envelope through public definition and transport seams.</summary>
public sealed class DefinitionEnvelopeRoundTripTests
{
    [Fact(DisplayName = "FormDefinition legacy metadata resolves through its definition envelope")]
    public void FormDefinition_legacy_metadata_resolves_through_envelope()
    {
        var lineage = new FormDefinitionLineage(
            new FormDefinitionId("forms/base-inspection"),
            new SemanticVersion(2, 3, 4));
        var envelope = new DefinitionEnvelope<
            FormDefinitionId,
            SemanticVersion,
            TenantId,
            FormDefinitionProvenance>(
                Identity: new FormDefinitionId("forms/annual-inspection"),
                Version: new SemanticVersion(3, 1, 0),
                Tenant: new TenantId("tenant-envelope-proof"),
                CascadeLayer: CascadeLayer.Tenant,
                Provenance: new FormDefinitionProvenance(new IdentityRef("party", "form-author"), lineage),
                Requires: Array.Empty<DefinitionRequirement>());
        var now = new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

        var definition = new FormDefinition(
            Envelope: envelope,
            Status: FormDefinitionStatus.Draft,
            SchemaRef: new SchemaId("sha256:definition-envelope-proof"),
            Overlay: EmptyOverlay(),
            CreatedAt: now,
            UpdatedAt: now);

        Assert.Equal(envelope, definition.Envelope);
        Assert.Equal(envelope.Identity, definition.Id);
        Assert.Equal(envelope.Version, definition.Version);
        Assert.Equal(envelope.Tenant, definition.Tenant);
        Assert.Equal(envelope.Provenance.Owner, definition.Owner);
        Assert.Equal(envelope.Provenance.Lineage, definition.Lineage);
        Assert.Empty(definition.Envelope.Requires);

        var nextVersion = new SemanticVersion(3, 1, 1);
        var revised = definition with { Version = nextVersion };

        Assert.Equal(nextVersion, revised.Envelope.Version);
        Assert.Equal(nextVersion, revised.Version);
        Assert.Equal(new SemanticVersion(3, 1, 0), definition.Envelope.Version);
    }

    [Fact(DisplayName = "authored form envelope survives storage, pack export, install, and read back")]
    public async Task Authored_form_envelope_survives_storage_pack_export_install_and_read_back()
    {
        var tenant = new TenantId("tenant-envelope-round-trip");
        var version = new SemanticVersion(4, 2, 1);
        var id = new FormDefinitionId("forms/envelope-round-trip");
        var parentId = new FormDefinitionId("forms/envelope-parent");
        var parentVersion = new SemanticVersion(1, 0, 0);
        var lineage = new FormDefinitionLineage(parentId, parentVersion);
        var envelope = new DefinitionEnvelope<
            FormDefinitionId,
            SemanticVersion,
            TenantId,
            FormDefinitionProvenance>(
                Identity: id,
                Version: version,
                Tenant: tenant,
                CascadeLayer: CascadeLayer.Tenant,
                Provenance: new FormDefinitionProvenance(new IdentityRef("party", "alice"), lineage),
                Requires: Array.Empty<DefinitionRequirement>());
        var now = new DateTimeOffset(2026, 8, 18, 13, 0, 0, TimeSpan.Zero);
        var sourceSchemas = new InMemorySchemaRegistry(TimeProvider.System);
        var schema = await sourceSchemas.RegisterAsync(FormSchemaJson);
        using var sourceForms = new InMemoryFormDefinitionStore(TimeProvider.System);
        await sourceForms.RegisterAsync(new FormDefinition(
            Id: parentId,
            Version: parentVersion,
            Status: FormDefinitionStatus.Draft,
            Tenant: tenant,
            Owner: new IdentityRef("party", "alice"),
            SchemaRef: schema.Id,
            Overlay: ValidOverlay(),
            Lineage: null,
            CreatedAt: now,
            UpdatedAt: now));
        await sourceForms.PublishAsync(new DefinitionCoordinates(
            tenant, parentId.Value, parentVersion.ToString()));
        await sourceForms.RegisterAsync(new FormDefinition(
            Envelope: envelope,
            Status: FormDefinitionStatus.Draft,
            SchemaRef: schema.Id,
            Overlay: ValidOverlay(),
            CreatedAt: now,
            UpdatedAt: now));
        await sourceForms.PublishAsync(new DefinitionCoordinates(tenant, id.Value, version.ToString()));

        using var registryServices = new ServiceCollection()
            .AddLogging()
            .AddInMemoryAssetTypeSystem()
            .BuildServiceProvider();
        var ceremony = new ComposeCeremony(
            registryServices.GetRequiredService<IEntityTypeRegistry>(),
            new PackContentCanonicalizer(),
            new PackDcpCanonicalizer(),
            new InMemoryDraftCompositionStore(),
            forms: sourceForms,
            schemas: sourceSchemas, clock: TimeProvider.System);
        var composed = await ceremony.ComposeAsync(
            new ComposeRequest(
                ComposeId: null,
                Key: "acme.envelope-round-trip",
                Version: "1.0.0",
                Name: "Envelope round trip",
                Description: "Definition envelope transport proof.",
                ScopeTier: PackScopeTier.Horizontal,
                TypeIds: Array.Empty<string>(),
                Dcp: DomainComplianceProfile.General("party:alice"),
                FormIds: [parentId.Value, id.Value]),
            tenant);
        var draft = Assert.IsType<DraftComposition>(composed.Draft);
        Assert.Equal(ComposeStatus.Ok, ceremony.Affirm(draft.ComposeId, tenant, draft.SnapshotHash).Status);

        using var keyPair = KeyPair.Generate();
        var signer = new Ed25519Signer(keyPair);
        var codec = new PackFileCodec();
        var exporter = NewExporter(codec);
        var exported = await ceremony.ExportAsync(draft.ComposeId, tenant, exporter, signer, epoch: 1);
        var packBytes = Assert.IsType<byte[]>(exported.FileBytes);

        var targetPackStore = new InMemoryPackInstallStore();
        var trustStore = new InMemoryPackTrustStore(
        [
            new PackTrustRoot(TrustScope.OwnRoster, keyPair.PrincipalId, 1, TrustRootStatus.Current),
        ]);
        var installer = new PackInstaller(
            new PackVerifier(new Ed25519Verifier(), codec),
            targetPackStore,
            new WorkflowRefusingPackContentAdmission(),
            new InMemoryPackInstallAudit(),
            Harborline.Api.LocalNodeHost.Tests.Authorization.TestAuthorization.AllowGate());
        var install = installer.Install(
            packBytes,
            new PackInstallContext(
                tenant,
                trustStore,
                PackRevocationList.Empty,
                now,
                TimeSpan.FromDays(30),
                Principal: "test-operator"));
        Assert.True(install.Installed, string.Join(",", install.RefusalCodes));
        Assert.True(installer.Activate(tenant, "acme.envelope-round-trip", "1.0.0", now, "test-operator").Activated);

        using var targetForms = new InMemoryFormDefinitionStore(TimeProvider.System);
        var targetSchemas = new InMemorySchemaRegistry(TimeProvider.System);
        var projector = new PackSeedProjector(
            targetPackStore,
            registryServices.GetRequiredService<IEntityTypeRegistry>(),
            NullLogger<PackSeedProjector>.Instance,
            forms: targetForms,
            schemas: targetSchemas,
            time: TimeProvider.System,
            authorizedForms: TestAuthorization.FormLifecycle(
                targetForms, Harborline.Api.LocalNodeHost.Tests.Authorization.TestAuthorization.AllowGate(),
                Harborline.Api.LocalNodeHost.Tests.Authorization.TestAuthorization.RoleGate()));
        var projected = await projector.ProjectActivePacksAsync(tenant);
        Assert.Equal(2, projected.FormDefinitionsPublished);

        var restored = await targetForms.GetAsync(new DefinitionCoordinates(tenant, id.Value, version.ToString()));

        Assert.Equal(envelope.Identity, restored.Envelope.Identity);
        Assert.Equal(envelope.Version, restored.Envelope.Version);
        Assert.Equal(envelope.Tenant, restored.Envelope.Tenant);
        Assert.Equal(CascadeLayer.Pack, restored.Envelope.CascadeLayer);
        Assert.Equal(envelope.Provenance.Owner, restored.Envelope.Provenance.Owner);
        Assert.Equal(envelope.Provenance.Lineage, restored.Envelope.Provenance.Lineage);
        Assert.Empty(restored.Envelope.Requires);
    }

    private static HarborlineOverlay EmptyOverlay() => new(
        Fields: new Dictionary<string, FieldOverlay>(),
        Sections: Array.Empty<FormSection>(),
        Rules: Array.Empty<RuleDefinition>());

    private const string FormSchemaJson = """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": {
            "name": { "type": "string" }
          },
          "required": ["name"]
        }
        """;

    private static HarborlineOverlay ValidOverlay() => new(
        Fields: new Dictionary<string, FieldOverlay>
        {
            ["name"] = new(InternationalizedText.FromInvariant("Name"), ControlHint: "text"),
        },
        Sections:
        [
            new FormSection(
                Id: "main",
                Title: InternationalizedText.FromInvariant("Main"),
                Fields: ["name"],
                Access: new SectionAccess(ReadRoles: [], WriteRoles: [])),
        ],
        Rules: Array.Empty<RuleDefinition>());

    private static PackExporter NewExporter(PackFileCodec codec) => new(
        new PackContentCanonicalizer(),
        new PackDcpCanonicalizer(),
        new PackValidator(new PackContentPiiScanner()),
        new DcpValidator(DcpCounselRegister.FromEmbeddedResource()),
        codec, timeProvider: TimeProvider.System);
}
