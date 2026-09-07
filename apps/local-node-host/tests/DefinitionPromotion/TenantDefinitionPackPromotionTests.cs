using Harborline.Api.Blocks.Assets.Registry.DependencyInjection;
using Harborline.Api.Blocks.Assets.Registry.Services;
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
using Harborline.Api.LocalNodeHost.Health;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harborline.Api.LocalNodeHost.Tests.DefinitionPromotion;

/// <summary>Proves tenant definitions use the existing pack promotion seam between nodes.</summary>
public sealed class TenantDefinitionPackPromotionTests
{
    [Fact(DisplayName = "draft definition history says it syncs to peers and is not safe staging")]
    public void Draft_definition_history_marks_peer_synced_and_not_safe_for_staging()
    {
        var tenant = new TenantId("tenant-draft-semantics");
        var now = new DateTimeOffset(2026, 8, 18, 14, 0, 0, TimeSpan.Zero);
        var draft = new FormDefinition(
            Id: new FormDefinitionId("forms/draft-semantics"),
            Version: new SemanticVersion(1, 0, 0),
            Status: FormDefinitionStatus.Draft,
            Tenant: tenant,
            Owner: new IdentityRef("party", "tenant-author"),
            SchemaRef: new SchemaId("sha256:draft-semantics"),
            Overlay: ValidOverlay(),
            Lineage: null,
            CreatedAt: now,
            UpdatedAt: now);

        var history = FormVersionSummaryDto.From(draft);

        Assert.True(history.SyncsToPeers);
        Assert.False(history.SafeForStaging);
    }

    [Fact(DisplayName = "tenant definition preview is non-mutating and activation promotes it to the target node")]
    public async Task Tenant_definition_preview_is_non_mutating_and_activation_promotes_to_target()
    {
        var tenant = new TenantId("tenant-pack-promotion");
        var definitionId = new FormDefinitionId("forms/tenant-authored-inspection");
        var definitionVersion = new SemanticVersion(2, 1, 0);
        var now = new DateTimeOffset(2026, 8, 18, 15, 0, 0, TimeSpan.Zero);

        var nodeASchemas = new InMemorySchemaRegistry(TimeProvider.System);
        var schema = await nodeASchemas.RegisterAsync(FormSchemaJson);
        using var nodeAForms = new InMemoryFormDefinitionStore(TimeProvider.System);
        await nodeAForms.RegisterAsync(new FormDefinition(
            Envelope: new DefinitionEnvelope<FormDefinitionId, SemanticVersion, TenantId, FormDefinitionProvenance>(
                Identity: definitionId,
                Version: definitionVersion,
                Tenant: tenant,
                CascadeLayer: CascadeLayer.Tenant,
                Provenance: new FormDefinitionProvenance(new IdentityRef("party", "tenant-author"), Lineage: null),
                Requires: Array.Empty<DefinitionRequirement>()),
            Status: FormDefinitionStatus.Draft,
            SchemaRef: schema.Id,
            Overlay: ValidOverlay(),
            CreatedAt: now,
            UpdatedAt: now));
        await nodeAForms.PublishAsync(new DefinitionCoordinates(
            tenant, definitionId.Value, definitionVersion.ToString()));

        using var registryServices = new ServiceCollection()
            .AddLogging()
            .AddInMemoryAssetTypeSystem()
            .BuildServiceProvider();
        var ceremony = new ComposeCeremony(
            registryServices.GetRequiredService<IEntityTypeRegistry>(),
            new PackContentCanonicalizer(),
            new PackDcpCanonicalizer(),
            new InMemoryDraftCompositionStore(),
            forms: nodeAForms,
            schemas: nodeASchemas, clock: TimeProvider.System);
        var composed = await ceremony.ComposeAsync(
            new ComposeRequest(
                ComposeId: null,
                Key: "tenant.authored.inspection",
                Version: "1.0.0",
                Name: "Tenant-authored inspection",
                Description: "Promotion proof.",
                ScopeTier: PackScopeTier.Horizontal,
                TypeIds: Array.Empty<string>(),
                Dcp: DomainComplianceProfile.General("party:tenant-author"),
                FormIds: [definitionId.Value]),
            tenant);
        var draft = Assert.IsType<DraftComposition>(composed.Draft);
        Assert.Equal(ComposeStatus.Ok, ceremony.Affirm(draft.ComposeId, tenant, draft.SnapshotHash).Status);

        using var keyPair = KeyPair.Generate();
        var codec = new PackFileCodec();
        var exported = await ceremony.ExportAsync(
            draft.ComposeId,
            tenant,
            NewExporter(codec),
            new Ed25519Signer(keyPair),
            epoch: 1);
        var packBytes = Assert.IsType<byte[]>(exported.FileBytes);

        var nodeBPackStore = new InMemoryPackInstallStore();
        var trustStore = new InMemoryPackTrustStore(
        [
            new PackTrustRoot(TrustScope.OwnRoster, keyPair.PrincipalId, 1, TrustRootStatus.Current),
        ]);
        var installer = new PackInstaller(
            new PackVerifier(new Ed25519Verifier(), codec),
            nodeBPackStore,
            new WorkflowRefusingPackContentAdmission(),
            new InMemoryPackInstallAudit(),
            Harborline.Api.LocalNodeHost.Tests.Authorization.TestAuthorization.AllowGate());
        var context = new PackInstallContext(
            tenant,
            trustStore,
            PackRevocationList.Empty,
            now,
            TimeSpan.FromDays(30),
            Principal: "test-operator");
        using var nodeBForms = new InMemoryFormDefinitionStore(TimeProvider.System);
        var nodeBSchemas = new InMemorySchemaRegistry(TimeProvider.System);
        var projector = new PackSeedProjector(
            nodeBPackStore,
            registryServices.GetRequiredService<IEntityTypeRegistry>(),
            NullLogger<PackSeedProjector>.Instance,
            forms: nodeBForms,
            schemas: nodeBSchemas,
            time: TimeProvider.System,
            authorizedForms: TestAuthorization.FormLifecycle(
                nodeBForms, Harborline.Api.LocalNodeHost.Tests.Authorization.TestAuthorization.AllowGate(),
                Harborline.Api.LocalNodeHost.Tests.Authorization.TestAuthorization.RoleGate()));

        var preview = installer.Preview(packBytes, context);

        Assert.Equal(PackInstallVerdict.WouldInstall, preview.Verdict);
        Assert.Equal([definitionId.Value], preview.NewSeedKeys);
        Assert.Null(nodeBPackStore.GetActive(tenant, "tenant.authored.inspection"));
        Assert.Null(await nodeBForms.GetCurrentPublishedAsync(new DefinitionAddress(tenant, definitionId.Value)));
        Assert.Equal(0, (await projector.ProjectActivePacksAsync(tenant)).FormDefinitionsPublished);

        var install = installer.Install(packBytes, context);

        Assert.True(install.Installed, string.Join(",", install.RefusalCodes));
        Assert.Null(nodeBPackStore.GetActive(tenant, "tenant.authored.inspection"));
        Assert.Null(await nodeBForms.GetCurrentPublishedAsync(new DefinitionAddress(tenant, definitionId.Value)));
        Assert.Equal(0, (await projector.ProjectActivePacksAsync(tenant)).FormDefinitionsPublished);

        var activation = installer.Activate(tenant, "tenant.authored.inspection", "1.0.0", now, "test-operator");
        var projection = await projector.ProjectActivePacksAsync(tenant);
        var promoted = await nodeBForms.GetCurrentPublishedAsync(new DefinitionAddress(tenant, definitionId.Value));

        Assert.True(activation.Activated, activation.Error);
        Assert.Equal(1, projection.FormDefinitionsPublished);
        Assert.NotNull(promoted);
        Assert.Equal(definitionVersion, promoted.Version);
        Assert.Equal(tenant, promoted.Tenant);
        Assert.Equal(new IdentityRef("party", "tenant-author"), promoted.Owner);
    }

    private static PackExporter NewExporter(PackFileCodec codec) => new(
        new PackContentCanonicalizer(),
        new PackDcpCanonicalizer(),
        new PackValidator(new PackContentPiiScanner()),
        new DcpValidator(DcpCounselRegister.FromEmbeddedResource()),
        codec, timeProvider: TimeProvider.System);

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
}
