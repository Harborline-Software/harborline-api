using System.Text.Json.Nodes;

using Harborline.Api.Blocks.Assets.Registry.Model;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.Packs.Install;

namespace Harborline.Api.LocalNodeHost.Data.PackProjection;

/// <summary>
/// Parses + validates the PINNED canonical JSON contract for a pack content item of kind
/// <see cref="Harborline.Api.Foundation.Packs.Model.PackContentKind.AssetTypeDefinition"/> into a shared
/// <see cref="EntityTypeSeed"/> at <see cref="CascadeLayer.Pack"/> provenance (design note
/// <c>_shared/engineering/pack-content-schemas-2026-07-06.md</c>). A first-party pack ships one such
/// object per baseline asset type; the node's <see cref="PackSeedProjector"/> calls
/// <see cref="TryParse"/> at install-activate time and, on a HIT, seeds the shared registry the
/// <c>GET /asset-registry/types</c> dropdown reads.
/// </summary>
/// <remarks>
/// <para>
/// <b>Provenance is NEVER read from content.</b> A pack cannot self-declare a <see cref="CascadeLayer.Base"/>
/// (platform-authoritative) or <see cref="CascadeLayer.Tenant"/> layer — the projector alone stamps
/// <see cref="CascadeLayer.Pack"/>. So this parser deliberately ignores any <c>provenance</c> field in
/// the content and the caller supplies the layer.
/// </para>
/// <para>
/// <b>Validation mirrors the Type Manager route</b> (<c>AssetRegistryRoutes.TryBuildDescriptor</c>): at
/// least one trait, a condition scale of at least 2, and a non-negative useful-life. A malformed item is
/// a <see cref="TryParse"/> miss (the projector skips it with a loud log, never bricking the install) —
/// NOT an exception, so one bad type in a pack cannot deny-of-service every other type it ships.
/// </para>
/// </remarks>
internal static class PackAssetTypeContent
{
    /// <summary>
    /// Attempts to parse an <c>AssetTypeDefinition</c> content body into its <see cref="EntityTypeId"/>
    /// and <see cref="EntityTypeDescriptor"/>. Returns <see langword="false"/> with a human-readable
    /// <paramref name="error"/> code on any malformed / invalid field (the projector logs + skips).
    /// </summary>
    /// <param name="content">The canonical content body.</param>
    /// <param name="declaredVersion">The version the SIGNED manifest declares for this leaf — the pinned
    /// content-shape version (see <see cref="FormBindingShapeVersion"/>). A body carrying either form-binding
    /// field under an older declared version is refused by name, never read.</param>
    /// <param name="formVersionByKey">Resolves a pack-local <c>FormDefinition</c> content key to the version
    /// that item declares — the SAME (key, version) tuple <see cref="PackSeedProjector"/> publishes the form
    /// under, so the binding rides ONE key space and never a second one. <see langword="null"/> means no
    /// sibling items are available (the retraction path, which needs only the id) and a binding then refuses
    /// rather than resolving to a guess.</param>
    /// <param name="id">The parsed type id.</param>
    /// <param name="descriptor">The parsed descriptor.</param>
    /// <param name="error">The refusal reason on a miss.</param>
    public static bool TryParse(
        JsonNode? content,
        string declaredVersion,
        Func<string, string?>? formVersionByKey,
        out EntityTypeId id,
        out EntityTypeDescriptor descriptor,
        out string error)
    {
        id = default;
        descriptor = null!;
        error = string.Empty;

        if (content is not JsonObject obj)
        {
            error = "content is not a JSON object";
            return false;
        }

        var idStr = ReadString(obj, "id");
        if (string.IsNullOrWhiteSpace(idStr))
        {
            error = "missing/blank 'id'";
            return false;
        }

        var displayName = ReadString(obj, "displayName");
        if (string.IsNullOrWhiteSpace(displayName))
        {
            error = "missing/blank 'displayName'";
            return false;
        }

        // Traits: at least one of container / maintainable / movable (mirrors at_least_one_trait_required).
        var traits = EntityTrait.None;
        if (obj["traits"] is JsonArray traitArr)
        {
            foreach (var t in traitArr)
            {
                switch (t?.GetValue<string>().Trim().ToLowerInvariant())
                {
                    case "container": traits |= EntityTrait.Container; break;
                    case "maintainable": traits |= EntityTrait.Maintainable; break;
                    case "movable": traits |= EntityTrait.Movable; break;
                    default: /* unknown trait token ignored (forward-compatible) */ break;
                }
            }
        }

        if (traits == EntityTrait.None)
        {
            error = "'traits' must carry at least one of container/maintainable/movable";
            return false;
        }

        // Optional condition scale (>= 2 when present, matching ConditionRating).
        int? conditionScaleMax = ReadInt(obj, "conditionScaleMax");
        if (conditionScaleMax is { } scale && scale < 2)
        {
            error = "'conditionScaleMax' must be at least 2";
            return false;
        }

        // Optional read-side capital-planning default (>= 0 when present).
        int? life = ReadInt(obj, "expectedUsefulLifeYears");
        if (life is { } l && l < 0)
        {
            error = "'expectedUsefulLifeYears' must be non-negative";
            return false;
        }

        var parentType = ReadString(obj, "parentType") is { Length: > 0 } p
            ? new EntityTypeId(p.Trim())
            : (EntityTypeId?)null;

        var disciplines = obj["disciplines"] is JsonArray discArr
            ? discArr
                .Select(d => d?.GetValue<string>())
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Select(d => new DisciplineTag(d!.Trim()))
                .ToList()
            : null;

        if (!TryReadPropertyFormBinding(obj, declaredVersion, formVersionByKey, out var propertyForm, out error))
        {
            return false;
        }
        if (!TryReadInspectionFormBindings(obj, declaredVersion, formVersionByKey, out var inspectionForms, out error))
        {
            return false;
        }

        id = new EntityTypeId(idStr.Trim());
        descriptor = new EntityTypeDescriptor(
            DisplayName: displayName.Trim(),
            Traits: traits,
            ParentType: parentType,
            PropertyFormBinding: propertyForm,
            Disciplines: disciplines,
            InspectionFormBindings: inspectionForms,
            ExpectedUsefulLifeYears: life,
            TypicalReplacementCost: null,
            ConditionScaleMax: conditionScaleMax);
        return true;
    }

    /// <summary>
    /// The EXACT inverse of <see cref="TryParse"/> (B-2a): projects a live registry
    /// <see cref="EntityTypeDescriptor"/> back into the pinned canonical <c>AssetTypeDefinition</c> JSON so
    /// the Pack Composer can SNAPSHOT an authored type into a pack leaf (design note §3.1). Emits ONLY the
    /// fields <see cref="TryParse"/> reads, so <c>parse → emit → parse</c> is byte-lossless for any type
    /// that uses those fields (the #127 General pack does). The result is fed to
    /// <c>PackContentCanonicalizer</c> (the ONE canonicalizer, S-14), so the leaf's content-address is the
    /// type's CANONICAL address.
    /// </summary>
    /// <remarks>
    /// <b>Traits emit in a deterministic canonical order</b> (enum order: Container → Maintainable → Movable),
    /// NOT the incidental array order a hand-authored pack may carry. A trait-flags value cannot recover the
    /// source array's order, so a Composer-produced leaf's content-address matches the type's CANONICAL
    /// address — never the raw hand-authored bytes of a multi-trait type (this is the acceptance's "content
    /// -address match, NOT byte-identity"). Null-valued optional fields are OMITTED (never emitted as
    /// <c>null</c>), matching how a hand-authored pack omits them, so the canonical shapes coincide.
    /// </remarks>
    public static JsonObject ToContent(EntityTypeId id, EntityTypeDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var obj = new JsonObject
        {
            ["id"] = id.Value,
            ["displayName"] = descriptor.DisplayName,
        };

        // Deterministic enum order — Container(1) → Maintainable(2) → Movable(4).
        var traits = new JsonArray();
        if (descriptor.Traits.HasFlag(EntityTrait.Container)) traits.Add((JsonNode)"Container");
        if (descriptor.Traits.HasFlag(EntityTrait.Maintainable)) traits.Add((JsonNode)"Maintainable");
        if (descriptor.Traits.HasFlag(EntityTrait.Movable)) traits.Add((JsonNode)"Movable");
        obj["traits"] = traits;

        if (descriptor.ParentType is { } parent && !string.IsNullOrWhiteSpace(parent.Value))
        {
            obj["parentType"] = parent.Value;
        }

        // The property-form binding travels as the bound form's PACK CONTENT KEY — the same key the
        // FormDefinition leaf carries, which install publishes the form under. The pinned VERSION is not
        // re-stated here: it is the sibling leaf's declared version (one key space, no second pin to drift).
        if (descriptor.PropertyFormBinding is { } propertyForm
            && !string.IsNullOrWhiteSpace(propertyForm.Definition.Value))
        {
            obj["propertyFormBinding"] = propertyForm.Definition.Value;
        }

        if (descriptor.InspectionFormBindings.Count > 0)
        {
            var bindings = new JsonObject();
            foreach (var (discipline, form) in descriptor.InspectionFormBindings
                         .OrderBy(pair => pair.Key.Value, StringComparer.Ordinal))
            {
                if (!string.IsNullOrWhiteSpace(discipline.Value) && !string.IsNullOrWhiteSpace(form.Definition.Value))
                {
                    bindings[discipline.Value] = form.Definition.Value;
                }
            }
            if (bindings.Count > 0) obj["inspectionFormBindings"] = bindings;
        }

        if (descriptor.Disciplines is { Count: > 0 } disciplines)
        {
            var arr = new JsonArray();
            foreach (var d in disciplines)
            {
                if (!string.IsNullOrWhiteSpace(d.Value)) arr.Add((JsonNode)d.Value);
            }
            if (arr.Count > 0) obj["disciplines"] = arr;
        }

        if (descriptor.ExpectedUsefulLifeYears is { } life) obj["expectedUsefulLifeYears"] = life;
        if (descriptor.ConditionScaleMax is { } scale) obj["conditionScaleMax"] = scale;

        return obj;
    }

    /// <summary>
    /// The form-binding field tokens a live descriptor may SET that <see cref="ToContent"/> cannot carry.
    /// Compose-time detection (#141): a Composer that snapshots such a type would DROP these bindings SILENTLY;
    /// the ceremony instead surfaces a
    /// <see cref="Harborline.Api.LocalNodeHost.Data.Compose.ComposeWarningCodes.ProjectionLossyFormBinding"/>
    /// warning carrying this list.
    /// </summary>
    /// <remarks>
    /// Kept next to <see cref="ToContent"/> ON PURPOSE: this list is exactly the descriptor fields that
    /// method omits. The current versioned shape carries both form-binding fields, so this is empty; it remains
    /// the single compose inventory for future descriptor binding fields.
    /// </remarks>
    public static IReadOnlyList<string> DroppedFormBindingFields(EntityTypeDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        return Array.Empty<string>();
    }

    /// <summary>
    /// The content-shape version that introduced <c>propertyFormBinding</c>. A leaf's pinned shape version is
    /// the version its SIGNED manifest ref declares — the field <c>PackValidationCodes.ContentVersionUnpinned</c>
    /// requires to be a pinned semver, and the one <see cref="PackSeedProjector"/> already cross-checks a body
    /// against (its rule-version and taxonomy-version agreement checks). Every hand-authored first-party
    /// <c>AssetTypeDefinition</c> leaf declares <c>1.0.0</c>, so this additive field is a MINOR bump of that
    /// declared version: a <c>1.0.0</c> leaf that OMITS the field still parses, and a <c>1.0.0</c> leaf that
    /// CARRIES it is refused by name rather than read under a shape it never declared.
    /// </summary>
    public const string FormBindingShapeVersion = "1.1.0";

    /// <summary>The content-shape version that introduced the <c>inspectionFormBindings</c> map.</summary>
    public const string InspectionFormBindingShapeVersion = "1.2.0";

    /// <summary>
    /// The content-shape version a leaf carrying <paramref name="descriptor"/> must declare, given the
    /// <paramref name="declaredVersion"/> its composer would otherwise stamp. It selects the newest shape
    /// needed by either binding; an unbound type keeps the composer's version unchanged.
    /// </summary>
    public static string ContentVersionFor(EntityTypeDescriptor descriptor, string declaredVersion)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var requiredVersion = descriptor.InspectionFormBindings.Count > 0
            ? InspectionFormBindingShapeVersion
            : descriptor.PropertyFormBinding is not null
                ? FormBindingShapeVersion
                : declaredVersion;
        return PackVersion.Compare(declaredVersion, requiredVersion) >= 0 ? declaredVersion : requiredVersion;
    }

    private static bool TryReadPropertyFormBinding(
        JsonObject obj,
        string declaredVersion,
        Func<string, string?>? formVersionByKey,
        out FormBindingRef? binding,
        out string error)
    {
        binding = null;
        error = string.Empty;

        var key = ReadString(obj, "propertyFormBinding")?.Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            // Absent (or blank) means an UNBOUND type — including every pack authored before the field existed.
            return true;
        }

        if (PackVersion.Compare(declaredVersion ?? string.Empty, FormBindingShapeVersion) < 0)
        {
            error = "'propertyFormBinding' requires a declared content version of at least "
                    + $"{FormBindingShapeVersion} (this leaf declares '{declaredVersion}')";
            return false;
        }

        var formVersion = formVersionByKey?.Invoke(key);
        if (string.IsNullOrWhiteSpace(formVersion))
        {
            error = $"'propertyFormBinding' names '{key}', which is not a FormDefinition in this pack";
            return false;
        }

        SemanticVersion pinned;
        try
        {
            pinned = SemanticVersion.Parse(formVersion!);
        }
        catch (FormatException)
        {
            error = $"the FormDefinition '{key}' this type binds declares an unparseable version '{formVersion}'";
            return false;
        }

        binding = new FormBindingRef(new FormDefinitionId(key), pinned);
        return true;
    }

    private static bool TryReadInspectionFormBindings(
        JsonObject obj,
        string declaredVersion,
        Func<string, string?>? formVersionByKey,
        out IReadOnlyDictionary<DisciplineTag, FormBindingRef> bindings,
        out string error)
    {
        bindings = new Dictionary<DisciplineTag, FormBindingRef>();
        error = string.Empty;
        if (obj["inspectionFormBindings"] is null)
        {
            return true;
        }
        if (obj["inspectionFormBindings"] is not JsonObject entries)
        {
            error = "'inspectionFormBindings' must be a JSON object";
            return false;
        }
        if (PackVersion.Compare(declaredVersion ?? string.Empty, InspectionFormBindingShapeVersion) < 0)
        {
            error = "'inspectionFormBindings' requires a declared content version of at least "
                    + $"{InspectionFormBindingShapeVersion} (this leaf declares '{declaredVersion}')";
            return false;
        }

        var parsed = new Dictionary<DisciplineTag, FormBindingRef>();
        foreach (var (rawDiscipline, node) in entries)
        {
            var discipline = rawDiscipline.Trim();
            var key = node is JsonValue value && value.TryGetValue<string>(out var formKey)
                ? formKey.Trim()
                : string.Empty;
            if (string.IsNullOrWhiteSpace(discipline) || string.IsNullOrWhiteSpace(key))
            {
                error = "'inspectionFormBindings' entries require non-blank discipline and FormDefinition key";
                return false;
            }
            var formVersion = formVersionByKey?.Invoke(key);
            if (string.IsNullOrWhiteSpace(formVersion))
            {
                error = $"'inspectionFormBindings' names '{key}', which is not a FormDefinition in this pack";
                return false;
            }
            try
            {
                parsed.Add(new DisciplineTag(discipline), new FormBindingRef(new FormDefinitionId(key), SemanticVersion.Parse(formVersion)));
            }
            catch (FormatException)
            {
                error = $"the FormDefinition '{key}' this type binds declares an unparseable version '{formVersion}'";
                return false;
            }
        }
        bindings = parsed;
        return true;
    }

    private static string? ReadString(JsonObject obj, string key)
        => obj.TryGetPropertyValue(key, out var node) && node is JsonValue v && v.TryGetValue<string>(out var s)
            ? s
            : null;

    private static int? ReadInt(JsonObject obj, string key)
        => obj.TryGetPropertyValue(key, out var node) && node is JsonValue v && v.TryGetValue<int>(out var i)
            ? i
            : null;
}
