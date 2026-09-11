using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Blocks.Assets.Registry.DependencyInjection;
using Harborline.Api.Blocks.Assets.Registry.Model;
using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Blobs;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.Packs.Dcp;
using Harborline.Api.Foundation.Packs.Export;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.Packs.Serialization;
using Harborline.Api.Foundation.Packs.Validation;
using Harborline.Api.Kernel.Schema;
using Harborline.Api.LocalNodeHost.Data.Compose;
using Harborline.Api.LocalNodeHost.Data.PackProjection;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

/// <summary>
/// The Pack Composer B-2a ceremony (<see cref="ComposeCeremony"/>) — the server-side security fold. Proves
/// snapshot-at-compose (Q3), the affirmation bound to the snapshot hash (S-1), and every fail-closed gate:
/// export-without-affirmation, affirm-with-wrong-hash, re-compose-after-affirm (snapshot-mismatch), the
/// DCP-absent refusal, and the non-general-class refusal (Q1).
/// </summary>
public sealed class ComposeCeremonyTests
{
    private static readonly TenantId Tenant = new("compose-test");
    private const string Principal = "author-principal";

    private static (ComposeCeremony Ceremony, IPackExporter Exporter, IOperationSigner Signer) NewFixture()
    {
        var provider = new ServiceCollection().AddLogging().AddInMemoryAssetTypeSystem().BuildServiceProvider();
        var registry = provider.GetRequiredService<IEntityTypeRegistry>();
        GeneralPackFixture.SeedInto(registry);

        var ceremony = new ComposeCeremony(
            registry, new PackContentCanonicalizer(), new PackDcpCanonicalizer(), new InMemoryDraftCompositionStore(), clock: TimeProvider.System);

        var exporter = new PackExporter(
            new PackContentCanonicalizer(), new PackDcpCanonicalizer(),
            new PackValidator(new PackContentPiiScanner()),
            new DcpValidator(DcpCounselRegister.FromEmbeddedResource()), new PackFileCodec(), timeProvider: TimeProvider.System);

        var signer = new Ed25519Signer(KeyPair.Generate());
        return (ceremony, exporter, signer);
    }

    private static DomainComplianceProfile GeneralDcp() => DomainComplianceProfile.General(Principal);

    private static ComposeRequest GeneralRequest(DomainComplianceProfile? dcp, string? composeId = null) => new(
        ComposeId: composeId,
        Key: GeneralPackFixture.PackKey,
        Version: GeneralPackFixture.PackVersion,
        Name: "General",
        Description: "Harborline General starter pack.",
        ScopeTier: PackScopeTier.Horizontal,
        TypeIds: GeneralPackFixture.TypeIds(),
        Dcp: dcp);

    // ── Snapshot-at-compose (Q3) + content-address determinism ─────────────────────────────────────────

    [Fact(DisplayName = "compose snapshots the six types at their canonical content-addresses")]
    public async Task Compose_snapshots_canonical_addresses()
    {
        var (ceremony, _, _) = NewFixture();

        var outcome = await ceremony.ComposeAsync(GeneralRequest(GeneralDcp()), Tenant);

        Assert.Equal(ComposeStatus.Ok, outcome.Status);
        var draft = outcome.Draft!;
        Assert.Equal(6, draft.Leaves.Count);

        // Each leaf's content-address is the type's CANONICAL address (compose emitted via ToContent).
        foreach (var (id, descriptor) in GeneralPackFixture.Descriptors())
        {
            var expectedCid = Cid.FromBytes(
                CanonicalJson.Serialize<JsonNode>(PackAssetTypeContent.ToContent(id, descriptor))).Value;
            var leaf = Assert.Single(draft.Leaves, l => l.Key == id.Value);
            Assert.Equal(expectedCid, leaf.ContentAddress);
            Assert.Equal(PackContentKind.AssetTypeDefinition, leaf.Kind);
        }
    }

    // ── Happy path: compose → affirm → export → carries the SAME six types ──────────────────────────────

    [Fact(DisplayName = "compose → affirm → export produces a signed pack carrying the same six types")]
    public async Task Compose_affirm_export_carries_six_types()
    {
        var (ceremony, exporter, signer) = NewFixture();

        var composed = await ceremony.ComposeAsync(GeneralRequest(GeneralDcp()), Tenant);
        Assert.Equal(ComposeStatus.Ok, composed.Status);
        var draft = composed.Draft!;

        var affirmed = ceremony.Affirm(draft.ComposeId, Tenant, draft.SnapshotHash);
        Assert.Equal(ComposeStatus.Ok, affirmed.Status);

        var exported = await ceremony.ExportAsync(draft.ComposeId, Tenant, exporter, signer, epoch: 1);
        Assert.Equal(ComposeStatus.Ok, exported.Status);
        Assert.NotNull(exported.FileBytes);

        // Decode the produced pack + parse each AssetTypeDefinition leaf → the SAME six (id, descriptor).
        var file = new PackFileCodec().TryDecode(exported.FileBytes!);
        Assert.NotNull(file);
        var assetLeaves = file!.Contents.Where(c => c.Kind == PackContentKind.AssetTypeDefinition).ToList();
        Assert.Equal(6, assetLeaves.Count);

        // Per-item content-address match: each payload's canonical bytes hash to the type's expected
        // canonical content-address (the acceptance's "same six types, per-item content-address match").
        var expectedCids = GeneralPackFixture.ExpectedCanonicalCids();
        foreach (var payload in assetLeaves)
        {
            var actualCid = Cid.FromBytes(Convert.FromBase64String(payload.ContentBase64)).Value;
            Assert.True(expectedCids.TryGetValue(payload.Key, out var expectedCid), $"unexpected type {payload.Key}");
            Assert.Equal(expectedCid, actualCid);
        }

        // The draft was consumed on success.
        Assert.Null(ceremony.GetDraft(draft.ComposeId, Tenant));
    }

    // ── Fail-closed: export before the human PII review (FU-1) ──────────────────────────────────────────

    [Fact(DisplayName = "export before affirmation is refused (human PII review required)")]
    public async Task Export_without_affirmation_refused()
    {
        var (ceremony, exporter, signer) = NewFixture();
        var draft = (await ceremony.ComposeAsync(GeneralRequest(GeneralDcp()), Tenant)).Draft!;

        var exported = await ceremony.ExportAsync(draft.ComposeId, Tenant, exporter, signer, epoch: 1);

        Assert.Equal(ComposeStatus.AffirmationRequired, exported.Status);
        Assert.Null(exported.FileBytes);
    }

    // ── S-1: the affirmation is bound to the snapshot hash ──────────────────────────────────────────────

    [Fact(DisplayName = "affirming a hash that isn't the current snapshot is refused (S-1)")]
    public async Task Affirm_with_wrong_hash_refused()
    {
        var (ceremony, _, _) = NewFixture();
        var draft = (await ceremony.ComposeAsync(GeneralRequest(GeneralDcp()), Tenant)).Draft!;

        var affirmed = ceremony.Affirm(draft.ComposeId, Tenant, "not-the-snapshot-hash");

        Assert.Equal(ComposeStatus.SnapshotMismatch, affirmed.Status);
    }

    [Fact(DisplayName = "re-composing after affirmation makes export refuse (snapshot-mismatch caught)")]
    public async Task Recompose_after_affirm_makes_export_refuse()
    {
        var (ceremony, exporter, signer) = NewFixture();

        // Compose the full six, affirm the snapshot.
        var draft = (await ceremony.ComposeAsync(GeneralRequest(GeneralDcp()), Tenant)).Draft!;
        Assert.Equal(ComposeStatus.Ok, ceremony.Affirm(draft.ComposeId, Tenant, draft.SnapshotHash).Status);

        // Re-compose the SAME id with a DIFFERENT selection (drop a type) — the snapshot hash changes.
        var reduced = new ComposeRequest(
            ComposeId: draft.ComposeId,
            Key: GeneralPackFixture.PackKey, Version: GeneralPackFixture.PackVersion,
            Name: "General", Description: "Harborline General starter pack.",
            ScopeTier: PackScopeTier.Horizontal,
            TypeIds: GeneralPackFixture.TypeIds().Take(5).ToList(),
            Dcp: GeneralDcp());
        var recomposed = await ceremony.ComposeAsync(reduced, Tenant);
        Assert.Equal(ComposeStatus.Ok, recomposed.Status);
        Assert.NotEqual(draft.SnapshotHash, recomposed.Draft!.SnapshotHash);

        // Export now refuses: the earlier affirmation was cleared by the re-compose (you sign what you inspect).
        var exported = await ceremony.ExportAsync(draft.ComposeId, Tenant, exporter, signer, epoch: 1);
        Assert.Equal(ComposeStatus.AffirmationRequired, exported.Status);
    }

    // ── Q1 DCP gate: absent + non-cleared class both hard-block at export ────────────────────────────────

    [Fact(DisplayName = "a pack with no DCP is refused at export (pack.dcp.missing)")]
    public async Task Dcp_absent_refused_at_export()
    {
        var (ceremony, exporter, signer) = NewFixture();

        var draft = (await ceremony.ComposeAsync(GeneralRequest(dcp: null), Tenant)).Draft!;
        ceremony.Affirm(draft.ComposeId, Tenant, draft.SnapshotHash);

        var exported = await ceremony.ExportAsync(draft.ComposeId, Tenant, exporter, signer, epoch: 1);

        Assert.Equal(ComposeStatus.ValidationFailed, exported.Status);
        Assert.Contains(exported.Errors, e => e.Code == "pack.dcp.missing");
    }

    // ── #141: compose-time detection of lossy asset-type projection (form-binding fields dropped) ────────

    [Fact(DisplayName = "composing the General pack (no form bindings) surfaces NO warnings — the lossless path")]
    public async Task Compose_lossless_types_has_no_warnings()
    {
        var (ceremony, _, _) = NewFixture();

        var outcome = await ceremony.ComposeAsync(GeneralRequest(GeneralDcp()), Tenant);

        Assert.Equal(ComposeStatus.Ok, outcome.Status);
        Assert.Empty(outcome.Draft!.Warnings);
    }

    [Fact(DisplayName = "composing a type whose form binding the pack cannot carry surfaces a structured warning")]
    public async Task Compose_lossy_form_binding_surfaces_warning()
    {
        var provider = new ServiceCollection().AddLogging().AddInMemoryAssetTypeSystem().BuildServiceProvider();
        var registry = provider.GetRequiredService<IEntityTypeRegistry>();

        // A type that DECLARES form bindings the pinned AssetTypeDefinition content shape cannot carry —
        // ToContent drops them, so compose must WARN rather than ship the type silently stripped.
        var typeId = new EntityTypeId("general.condenser");
        registry.SeedType(new EntityTypeSeed(
            typeId,
            new EntityTypeDescriptor(
                DisplayName: "Condenser",
                Traits: EntityTrait.Maintainable,
                PropertyFormBinding: new FormBindingRef("condenser-props", new SemanticVersion(1, 0, 0)),
                InspectionFormBindings: new Dictionary<DisciplineTag, FormBindingRef>
                {
                    [new DisciplineTag("electrical")] = new FormBindingRef("condenser-elec", new SemanticVersion(2, 0, 0)),
                }),
            CascadeLayer.Pack));

        var ceremony = new ComposeCeremony(
            registry, new PackContentCanonicalizer(), new PackDcpCanonicalizer(), new InMemoryDraftCompositionStore(), clock: TimeProvider.System);

        var request = new ComposeRequest(
            ComposeId: null, Key: "acme.hvac", Version: "1.0.0", Name: "HVAC", Description: "",
            ScopeTier: PackScopeTier.Horizontal, TypeIds: new[] { typeId.Value }, Dcp: GeneralDcp());

        var outcome = await ceremony.ComposeAsync(request, Tenant);

        // Compose still SUCCEEDS (the warning is advisory, not fail-closed) — but carries the structured finding.
        Assert.Equal(ComposeStatus.Ok, outcome.Status);
        var warning = Assert.Single(outcome.Draft!.Warnings);
        Assert.Equal(ComposeWarningCodes.ProjectionLossyFormBinding, warning.Code);
        Assert.Equal(typeId.Value, warning.Target);
        Assert.Equal("propertyFormBinding,inspectionFormBindings", warning.Params["fields"]);

        // Persisted on the draft: a reloaded Harborline App (GET) re-reads the same warning.
        Assert.Single(ceremony.GetDraft(outcome.Draft.ComposeId, Tenant)!.Warnings);
    }

    [Fact(DisplayName = "ticket 364: composing a type with its two inspection forms carries inspectionFormBindings at the 1.2.0 shape")]
    public async Task Compose_carries_inspection_form_bindings_when_the_forms_are_leaves()
    {
        var tenant = new TenantId("compose-binding");
        var now = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
        var formId = new FormDefinitionId("condenser-props");
        var electricalFormId = new FormDefinitionId("condenser-elec");
        var mechanicalFormId = new FormDefinitionId("condenser-mech");
        var formVersion = new SemanticVersion(1, 2, 0);

        var schemas = new InMemorySchemaRegistry(TimeProvider.System);
        var schema = await schemas.RegisterAsync(FormSchemaJson);
        using var forms = new InMemoryFormDefinitionStore(TimeProvider.System);
        await forms.RegisterAsync(new FormDefinition(
            Envelope: new DefinitionEnvelope<FormDefinitionId, SemanticVersion, TenantId, FormDefinitionProvenance>(
                Identity: formId,
                Version: formVersion,
                Tenant: tenant,
                CascadeLayer: CascadeLayer.Tenant,
                Provenance: new FormDefinitionProvenance(new IdentityRef("party", Principal), Lineage: null),
                Requires: Array.Empty<DefinitionRequirement>()),
            Status: FormDefinitionStatus.Draft,
            SchemaRef: schema.Id,
            Overlay: ValidOverlay(),
            CreatedAt: now,
            UpdatedAt: now));
        await forms.PublishAsync(new DefinitionCoordinates(tenant, formId.Value, formVersion.ToString()));
        foreach (var inspectionFormId in new[] { electricalFormId, mechanicalFormId })
        {
            await forms.RegisterAsync(new FormDefinition(
                Envelope: new DefinitionEnvelope<FormDefinitionId, SemanticVersion, TenantId, FormDefinitionProvenance>(
                    Identity: inspectionFormId,
                    Version: formVersion,
                    Tenant: tenant,
                    CascadeLayer: CascadeLayer.Tenant,
                    Provenance: new FormDefinitionProvenance(new IdentityRef("party", Principal), Lineage: null),
                    Requires: Array.Empty<DefinitionRequirement>()),
                Status: FormDefinitionStatus.Draft,
                SchemaRef: schema.Id,
                Overlay: ValidOverlay(),
                CreatedAt: now,
                UpdatedAt: now));
            await forms.PublishAsync(new DefinitionCoordinates(tenant, inspectionFormId.Value, formVersion.ToString()));
        }

        var provider = new ServiceCollection().AddLogging().AddInMemoryAssetTypeSystem().BuildServiceProvider();
        var registry = provider.GetRequiredService<IEntityTypeRegistry>();
        var typeId = new EntityTypeId("general.condenser");
        registry.SeedType(new EntityTypeSeed(
            typeId,
            new EntityTypeDescriptor(
                DisplayName: "Condenser",
                Traits: EntityTrait.Maintainable,
                PropertyFormBinding: new FormBindingRef(formId.Value, new SemanticVersion(1, 0, 0)),
                InspectionFormBindings: new Dictionary<DisciplineTag, FormBindingRef>
                {
                    [new DisciplineTag("electrical")] = new FormBindingRef(electricalFormId.Value, new SemanticVersion(1, 0, 0)),
                    [new DisciplineTag("mechanical")] = new FormBindingRef(mechanicalFormId.Value, new SemanticVersion(1, 0, 0)),
                }),
            CascadeLayer.Pack));

        var ceremony = new ComposeCeremony(
            registry, new PackContentCanonicalizer(), new PackDcpCanonicalizer(), new InMemoryDraftCompositionStore(),
            clock: TimeProvider.System, forms: forms, schemas: schemas);

        var outcome = await ceremony.ComposeAsync(
            new ComposeRequest(
                ComposeId: null, Key: "acme.hvac", Version: "1.0.0", Name: "HVAC", Description: "",
                ScopeTier: PackScopeTier.Horizontal, TypeIds: new[] { typeId.Value }, Dcp: GeneralDcp(),
                FormIds: new[] { formId.Value, electricalFormId.Value, mechanicalFormId.Value }),
            tenant);

        Assert.Equal(ComposeStatus.Ok, outcome.Status);
        var typeLeaf = Assert.Single(outcome.Draft!.Leaves, l => l.Kind == PackContentKind.AssetTypeDefinition);

        // (i) the binding travels as the form's PACK CONTENT KEY — the same key the FormDefinition leaf carries.
        Assert.Equal(
            formId.Value,
            typeLeaf.Content!.AsObject()["propertyFormBinding"]!.GetValue<string>());
        Assert.Contains(outcome.Draft.Leaves, l => l.Kind == PackContentKind.FormDefinition && l.Key == formId.Value);

        // (ii) every map entry travels as a form's pack content key and the leaf declares that shape.
        var inspectionBindings = typeLeaf.Content!.AsObject()["inspectionFormBindings"]!.AsObject();
        Assert.Equal(electricalFormId.Value, inspectionBindings["electrical"]!.GetValue<string>());
        Assert.Equal(mechanicalFormId.Value, inspectionBindings["mechanical"]!.GetValue<string>());
        Assert.Equal(PackAssetTypeContent.InspectionFormBindingShapeVersion, typeLeaf.Version);
        Assert.Equal("1.2.0", typeLeaf.Version);

        // (iii) neither carried binding is lossy.
        Assert.Empty(outcome.Draft.Warnings);
    }

    [Fact(DisplayName = "ticket 364: composing one of two inspection forms carries only the selected binding and persists a warning")]
    public async Task Compose_carries_only_the_selected_inspection_form_binding()
    {
        var tenant = new TenantId("compose-partial-binding");
        var now = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
        var formId = new FormDefinitionId("condenser-props");
        var electricalFormId = new FormDefinitionId("condenser-elec");
        var mechanicalFormId = new FormDefinitionId("condenser-mech");
        var formVersion = new SemanticVersion(1, 2, 0);

        var schemas = new InMemorySchemaRegistry(TimeProvider.System);
        var schema = await schemas.RegisterAsync(FormSchemaJson);
        using var forms = new InMemoryFormDefinitionStore(TimeProvider.System);
        foreach (var selectedFormId in new[] { formId, electricalFormId, mechanicalFormId })
        {
            await forms.RegisterAsync(new FormDefinition(
                Envelope: new DefinitionEnvelope<FormDefinitionId, SemanticVersion, TenantId, FormDefinitionProvenance>(
                    Identity: selectedFormId,
                    Version: formVersion,
                    Tenant: tenant,
                    CascadeLayer: CascadeLayer.Tenant,
                    Provenance: new FormDefinitionProvenance(new IdentityRef("party", Principal), Lineage: null),
                    Requires: Array.Empty<DefinitionRequirement>()),
                Status: FormDefinitionStatus.Draft,
                SchemaRef: schema.Id,
                Overlay: ValidOverlay(),
                CreatedAt: now,
                UpdatedAt: now));
            await forms.PublishAsync(new DefinitionCoordinates(tenant, selectedFormId.Value, formVersion.ToString()));
        }

        var provider = new ServiceCollection().AddLogging().AddInMemoryAssetTypeSystem().BuildServiceProvider();
        var registry = provider.GetRequiredService<IEntityTypeRegistry>();
        var typeId = new EntityTypeId("general.condenser");
        registry.SeedType(new EntityTypeSeed(
            typeId,
            new EntityTypeDescriptor(
                DisplayName: "Condenser",
                Traits: EntityTrait.Maintainable,
                PropertyFormBinding: new FormBindingRef(formId.Value, new SemanticVersion(1, 0, 0)),
                InspectionFormBindings: new Dictionary<DisciplineTag, FormBindingRef>
                {
                    [new DisciplineTag("electrical")] = new FormBindingRef(electricalFormId.Value, new SemanticVersion(1, 0, 0)),
                    [new DisciplineTag("mechanical")] = new FormBindingRef(mechanicalFormId.Value, new SemanticVersion(1, 0, 0)),
                }),
            CascadeLayer.Pack));

        var ceremony = new ComposeCeremony(
            registry, new PackContentCanonicalizer(), new PackDcpCanonicalizer(), new InMemoryDraftCompositionStore(),
            clock: TimeProvider.System, forms: forms, schemas: schemas);
        var outcome = await ceremony.ComposeAsync(
            new ComposeRequest(
                ComposeId: null, Key: "acme.hvac", Version: "1.0.0", Name: "HVAC", Description: "",
                ScopeTier: PackScopeTier.Horizontal, TypeIds: new[] { typeId.Value }, Dcp: GeneralDcp(),
                FormIds: new[] { formId.Value, electricalFormId.Value }),
            tenant);

        Assert.Equal(ComposeStatus.Ok, outcome.Status);
        var typeLeaf = Assert.Single(outcome.Draft!.Leaves, l => l.Kind == PackContentKind.AssetTypeDefinition);
        var inspectionBindings = typeLeaf.Content!.AsObject()["inspectionFormBindings"]!.AsObject();
        Assert.Equal(electricalFormId.Value, inspectionBindings["electrical"]!.GetValue<string>());
        Assert.False(inspectionBindings.ContainsKey("mechanical"));

        var warning = Assert.Single(outcome.Draft.Warnings);
        Assert.Equal(ComposeWarningCodes.ProjectionLossyFormBinding, warning.Code);
        Assert.Equal("inspectionFormBindings", warning.Params["fields"]);
        var persistedWarning = Assert.Single(ceremony.GetDraft(outcome.Draft.ComposeId, tenant)!.Warnings);
        Assert.Equal("inspectionFormBindings", persistedWarning.Params["fields"]);
    }

    [Fact(DisplayName = "a non-counsel-cleared RegulatoryClass hard-blocks at export (Q1)")]
    public async Task Non_general_class_refused_at_export()
    {
        var (ceremony, exporter, signer) = NewFixture();

        var financial = GeneralDcp() with { RegulatoryClass = RegulatoryClass.Financial };
        var draft = (await ceremony.ComposeAsync(GeneralRequest(financial), Tenant)).Draft!;
        ceremony.Affirm(draft.ComposeId, Tenant, draft.SnapshotHash);

        var exported = await ceremony.ExportAsync(draft.ComposeId, Tenant, exporter, signer, epoch: 1);

        Assert.Equal(ComposeStatus.ValidationFailed, exported.Status);
        Assert.Contains(exported.Errors, e => e.Code == "pack.dcp.regulatory_class.not_cleared");
    }

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
