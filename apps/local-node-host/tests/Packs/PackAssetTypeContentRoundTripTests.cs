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
            Assert.True(PackAssetTypeContent.TryParse(body, "1.0.0", null, out var id, out var descriptor, out var err), err);

            var emitted = PackAssetTypeContent.ToContent(id, descriptor);
            Assert.True(PackAssetTypeContent.TryParse(emitted, "1.0.0", null, out var id2, out var descriptor2, out var err2), err2);

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
        Assert.True(PackAssetTypeContent.TryParse(equipment, "1.0.0", null, out var id, out var descriptor, out _));

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
        Assert.True(PackAssetTypeContent.TryParse(vehicle, "1.0.0", null, out var id, out var descriptor, out _));

        var emitted = PackAssetTypeContent.ToContent(id, descriptor);
        var traits = emitted["traits"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        Assert.Equal(new[] { "Maintainable", "Movable" }, traits);

        // But it still parses back to the SAME type (order-insensitive on the flags).
        Assert.True(PackAssetTypeContent.TryParse(emitted, "1.0.0", null, out var id2, out var descriptor2, out _));
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
            Assert.True(PackAssetTypeContent.TryParse(body, "1.0.0", null, out _, out var descriptor, out _));
            Assert.Empty(PackAssetTypeContent.DroppedFormBindingFields(descriptor));
        }
    }

    [Fact(DisplayName = "ticket 357: a property-form binding EMITS as the bound form's pack content key and parses back")]
    public void Property_form_binding_round_trips()
    {
        var descriptor = new EntityTypeDescriptor(
            DisplayName: "Condenser",
            Traits: EntityTrait.Maintainable,
            PropertyFormBinding: new FormBindingRef("condenser-props", new SemanticVersion(1, 2, 0)));

        var emitted = PackAssetTypeContent.ToContent(new EntityTypeId("g.condenser"), descriptor);

        // The binding now TRAVELS (it used to be silently omitted) — as the pack-local content key only.
        Assert.Equal("condenser-props", emitted["propertyFormBinding"]!.GetValue<string>());
        // … so it is no longer reported as a dropped field.
        Assert.Empty(PackAssetTypeContent.DroppedFormBindingFields(descriptor));

        // Parse reads it back, resolving the PINNED version from the sibling FormDefinition leaf's declared
        // version — one key space, no second pin in the body.
        Assert.True(
            PackAssetTypeContent.TryParse(
                emitted,
                PackAssetTypeContent.FormBindingShapeVersion,
                key => key == "condenser-props" ? "1.2.0" : null,
                out _,
                out var parsed,
                out var error),
            error);
        Assert.Equal(new FormBindingRef("condenser-props", new SemanticVersion(1, 2, 0)), parsed.PropertyFormBinding);
    }

    [Fact(DisplayName = "ticket 357: a binding under the PREVIOUS declared content version is refused by name")]
    public void Property_form_binding_under_old_declared_version_is_refused()
    {
        var emitted = PackAssetTypeContent.ToContent(
            new EntityTypeId("g.condenser"),
            new EntityTypeDescriptor(
                DisplayName: "Condenser",
                Traits: EntityTrait.Maintainable,
                PropertyFormBinding: new FormBindingRef("condenser-props", new SemanticVersion(1, 0, 0))));

        Assert.False(
            PackAssetTypeContent.TryParse(
                emitted, "1.0.0", _ => "1.0.0", out _, out _, out var error));
        Assert.Contains(PackAssetTypeContent.FormBindingShapeVersion, error, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "ticket 357: a binding naming a form this pack does not carry is refused by name")]
    public void Property_form_binding_outside_the_pack_is_refused()
    {
        var emitted = PackAssetTypeContent.ToContent(
            new EntityTypeId("g.condenser"),
            new EntityTypeDescriptor(
                DisplayName: "Condenser",
                Traits: EntityTrait.Maintainable,
                PropertyFormBinding: new FormBindingRef("other-pack-form", new SemanticVersion(1, 0, 0))));

        Assert.False(
            PackAssetTypeContent.TryParse(
                emitted,
                PackAssetTypeContent.FormBindingShapeVersion,
                _ => null,
                out _,
                out _,
                out var error));
        Assert.Contains("not a FormDefinition in this pack", error, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "ticket 357: a type authored WITHOUT the field still parses on the previous version")]
    public void Unbound_type_still_parses_on_the_previous_version()
    {
        var body = GeneralPackFixture.RawContents().First(c => c.Key == "general.equipment").Content;

        Assert.True(PackAssetTypeContent.TryParse(body, "1.0.0", null, out _, out var descriptor, out var error), error);
        Assert.Null(descriptor.PropertyFormBinding);
    }

    [Fact(DisplayName = "ticket 364: inspection-form bindings emit as discipline-to-pack-form-key map and parse back")]
    public void Inspection_form_bindings_round_trip()
    {
        var descriptor = new EntityTypeDescriptor(
            DisplayName: "Condenser",
            Traits: EntityTrait.Maintainable,
            InspectionFormBindings: new Dictionary<DisciplineTag, FormBindingRef>
            {
                [new DisciplineTag("electrical")] = new FormBindingRef("condenser-elec", new SemanticVersion(2, 0, 0)),
                [new DisciplineTag("mechanical")] = new FormBindingRef("condenser-mech", new SemanticVersion(3, 1, 0)),
            });

        var emitted = PackAssetTypeContent.ToContent(new EntityTypeId("g.condenser"), descriptor);
        var bindings = emitted["inspectionFormBindings"]!.AsObject();
        Assert.Equal("condenser-elec", bindings["electrical"]!.GetValue<string>());
        Assert.Equal("condenser-mech", bindings["mechanical"]!.GetValue<string>());
        Assert.Empty(PackAssetTypeContent.DroppedFormBindingFields(descriptor));

        Assert.True(
            PackAssetTypeContent.TryParse(
                emitted,
                PackAssetTypeContent.InspectionFormBindingShapeVersion,
                key => key switch
                {
                    "condenser-elec" => "2.0.0",
                    "condenser-mech" => "3.1.0",
                    _ => null,
                },
                out _,
                out var parsed,
                out var error),
            error);
        Assert.Equal(
            new FormBindingRef("condenser-elec", new SemanticVersion(2, 0, 0)),
            parsed.InspectionFormBindings[new DisciplineTag("electrical")]);
        Assert.Equal(
            new FormBindingRef("condenser-mech", new SemanticVersion(3, 1, 0)),
            parsed.InspectionFormBindings[new DisciplineTag("mechanical")]);
    }

    [Fact(DisplayName = "both form binding shapes select the newest declared content version")]
    public void Content_version_bumps_for_all_form_bindings()
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
            PackAssetTypeContent.InspectionFormBindingShapeVersion,
            PackAssetTypeContent.ContentVersionFor(descriptor, "1.0.0"));
    }

    [Fact(DisplayName = "ticket 357: a bound type's leaf declares at least the form-binding shape version")]
    public void Content_version_bumps_for_a_bound_type()
    {
        var unbound = new EntityTypeDescriptor(DisplayName: "Widget", Traits: EntityTrait.Maintainable);
        var bound = unbound with
        {
            PropertyFormBinding = new FormBindingRef("widget-props", new SemanticVersion(1, 0, 0)),
        };

        Assert.Equal("1.0.0", PackAssetTypeContent.ContentVersionFor(unbound, "1.0.0"));
        Assert.Equal(
            PackAssetTypeContent.FormBindingShapeVersion,
            PackAssetTypeContent.ContentVersionFor(bound, "1.0.0"));
        // An already-newer composer version is preserved (never walked backwards).
        Assert.Equal("2.4.0", PackAssetTypeContent.ContentVersionFor(bound, "2.4.0"));
    }
}
