using System.Text.Json.Nodes;

using Harborline.Api.Blocks.Assets.Registry.Model;
using Harborline.Api.Foundation.Blobs;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.LocalNodeHost.Data.PackProjection;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

/// <summary>
/// The <see cref="PackAssetTypeContent.ToContent"/> emit path (Pack Composer B-2a) is the EXACT inverse of
/// <see cref="PackAssetTypeContent.TryParse"/> over the fields parse reads: <c>parse → emit → parse</c> is
/// lossless, and the emitted bytes are the type's CANONICAL content-address. Single-trait types match the
/// hand-authored bytes exactly; multi-trait types match the CANONICAL form (enum trait order), which is the
/// acceptance's "content-address match, NOT byte-identity".
/// </summary>
public sealed class PackAssetTypeContentRoundTripTests
{
    [Fact(DisplayName = "parse → emit → parse is lossless for all six General types")]
    public void RoundTrip_is_lossless()
    {
        foreach (var (key, body) in GeneralPackFixture.RawContents())
        {
            Assert.True(PackAssetTypeContent.TryParse(body, out var id, out var descriptor, out var err), err);

            var emitted = PackAssetTypeContent.ToContent(id, descriptor);
            Assert.True(PackAssetTypeContent.TryParse(emitted, out var id2, out var descriptor2, out var err2), err2);

            Assert.Equal(id, id2);
            // Same TYPE across the round-trip — compared by canonical content-address (descriptor record
            // equality compares its collection members by reference, so it can't be used here).
            Assert.Equal(GeneralPackFixture.CanonicalCid(id, descriptor), GeneralPackFixture.CanonicalCid(id2, descriptor2));
        }
    }

    [Fact(DisplayName = "a single-trait type's canonical content-address matches the hand-authored bytes exactly")]
    public void Single_trait_content_address_matches_hand_authored()
    {
        // Equipment carries ONE trait, so trait array order is not a factor — the canonical emit is byte-identical
        // to the hand-authored content, giving an EXACT per-item content-address match.
        var equipment = GeneralPackFixture.RawContents().First(c => c.Key == "general.equipment").Content;
        Assert.True(PackAssetTypeContent.TryParse(equipment, out var id, out var descriptor, out _));

        var handAuthoredCid = Cid.FromBytes(CanonicalJson.Serialize<JsonNode>(equipment)).Value;
        var composerCid = Cid.FromBytes(CanonicalJson.Serialize<JsonNode>(PackAssetTypeContent.ToContent(id, descriptor))).Value;

        Assert.Equal(handAuthoredCid, composerCid);
    }

    [Fact(DisplayName = "a multi-trait type emits traits in canonical enum order (Container → Maintainable → Movable)")]
    public void Multi_trait_emits_canonical_order()
    {
        // The hand-authored vehicle carries traits [Movable, Maintainable]; a flags value cannot recover that
        // order, so the canonical emit is [Maintainable, Movable]. Same TYPE (content-address match under the
        // canonicalizer), different raw bytes — the acceptance's "NOT byte-identity".
        var vehicle = GeneralPackFixture.RawContents().First(c => c.Key == "general.vehicle").Content;
        Assert.True(PackAssetTypeContent.TryParse(vehicle, out var id, out var descriptor, out _));

        var emitted = PackAssetTypeContent.ToContent(id, descriptor);
        var traits = emitted["traits"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        Assert.Equal(new[] { "Maintainable", "Movable" }, traits);

        // But it still parses back to the SAME type (order-insensitive on the flags).
        Assert.True(PackAssetTypeContent.TryParse(emitted, out var id2, out var descriptor2, out _));
        Assert.Equal(GeneralPackFixture.CanonicalCid(id, descriptor), GeneralPackFixture.CanonicalCid(id2, descriptor2));
    }

    [Fact(DisplayName = "emit omits null optional fields (no parentType / disciplines noise)")]
    public void Emit_omits_null_optional_fields()
    {
        var descriptor = new EntityTypeDescriptor(
            DisplayName: "Widget",
            Traits: EntityTrait.Maintainable,
            ConditionScaleMax: 5);

        var emitted = PackAssetTypeContent.ToContent(new EntityTypeId("general.widget"), descriptor);

        Assert.False(emitted.ContainsKey("parentType"));
        Assert.False(emitted.ContainsKey("disciplines"));
        Assert.False(emitted.ContainsKey("expectedUsefulLifeYears"));
        Assert.True(emitted.ContainsKey("conditionScaleMax"));
    }

    // ── #141 lossy-projection detection: ToContent cannot carry a type's form bindings ──────────────────

    [Fact(DisplayName = "a type with no form binding reports no dropped fields (the lossless common path)")]
    public void No_form_binding_reports_nothing_dropped()
    {
        // The #127 General types set neither form binding — the common path must warn nothing.
        foreach (var (_, body) in GeneralPackFixture.RawContents())
        {
            Assert.True(PackAssetTypeContent.TryParse(body, out _, out var descriptor, out _));
            Assert.Empty(PackAssetTypeContent.DroppedFormBindingFields(descriptor));
        }
    }

    [Fact(DisplayName = "a property-form binding is detected as a dropped field")]
    public void Property_form_binding_is_detected()
    {
        var descriptor = new EntityTypeDescriptor(
            DisplayName: "Condenser",
            Traits: EntityTrait.Maintainable,
            PropertyFormBinding: new FormBindingRef("condenser-props", new SemanticVersion(1, 0, 0)));

        // ToContent silently omits it (the lossy point) …
        Assert.False(PackAssetTypeContent.ToContent(new EntityTypeId("g.condenser"), descriptor)
            .ContainsKey("propertyFormBinding"));
        // … which detection surfaces.
        Assert.Equal(new[] { "propertyFormBinding" }, PackAssetTypeContent.DroppedFormBindingFields(descriptor));
    }

    [Fact(DisplayName = "an inspection-form binding is detected as a dropped field")]
    public void Inspection_form_binding_is_detected()
    {
        var descriptor = new EntityTypeDescriptor(
            DisplayName: "Condenser",
            Traits: EntityTrait.Maintainable,
            InspectionFormBindings: new Dictionary<DisciplineTag, FormBindingRef>
            {
                [new DisciplineTag("electrical")] = new FormBindingRef("condenser-elec", new SemanticVersion(2, 0, 0)),
            });

        Assert.Equal(new[] { "inspectionFormBindings" }, PackAssetTypeContent.DroppedFormBindingFields(descriptor));
    }

    [Fact(DisplayName = "both form bindings set → both tokens reported, in a stable order")]
    public void Both_form_bindings_reported()
    {
        var descriptor = new EntityTypeDescriptor(
            DisplayName: "Condenser",
            Traits: EntityTrait.Maintainable,
            PropertyFormBinding: new FormBindingRef("condenser-props", new SemanticVersion(1, 0, 0)),
            InspectionFormBindings: new Dictionary<DisciplineTag, FormBindingRef>
            {
                [new DisciplineTag("electrical")] = new FormBindingRef("condenser-elec", new SemanticVersion(2, 0, 0)),
            });

        Assert.Equal(
            new[] { "propertyFormBinding", "inspectionFormBindings" },
            PackAssetTypeContent.DroppedFormBindingFields(descriptor));
    }
}
