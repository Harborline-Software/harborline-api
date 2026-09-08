using System.Text.Json.Nodes;

using Harborline.Api.Blocks.Assets.Registry.Model;
using Harborline.Api.Foundation.Definitions;

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
    public static bool TryParse(
        JsonNode? content, out EntityTypeId id, out EntityTypeDescriptor descriptor, out string error)
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

        id = new EntityTypeId(idStr.Trim());
        descriptor = new EntityTypeDescriptor(
            DisplayName: displayName.Trim(),
            Traits: traits,
            ParentType: parentType,
            PropertyFormBinding: null,
            Disciplines: disciplines,
            InspectionFormBindings: null,
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
    /// The form-binding field tokens a live descriptor may SET that <see cref="ToContent"/> cannot carry
    /// (it emits ONLY the fields <see cref="TryParse"/> reads — the pinned <c>AssetTypeDefinition</c> content
    /// shape has no place for a form binding). Compose-time detection (#141): a Composer that snapshots such
    /// a type would DROP these bindings SILENTLY; the ceremony instead surfaces a
    /// <see cref="Harborline.Api.LocalNodeHost.Data.Compose.ComposeWarningCodes.ProjectionLossyFormBinding"/>
    /// warning carrying this list.
    /// </summary>
    /// <remarks>
    /// Kept next to <see cref="ToContent"/> ON PURPOSE: this list is exactly the descriptor fields that
    /// method omits. If a future content-schema revision teaches <see cref="ToContent"/> to emit one of these
    /// (and <see cref="TryParse"/> to read it back), REMOVE it here in the same change — otherwise a
    /// now-lossless field would raise a false warning. Returns an EMPTY list for a type that sets none (the
    /// #127 General pack — its six types carry no form binding — so the common path warns nothing).
    /// </remarks>
    public static IReadOnlyList<string> DroppedFormBindingFields(EntityTypeDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var dropped = new List<string>(capacity: 2);
        if (descriptor.PropertyFormBinding is not null)
        {
            dropped.Add("propertyFormBinding");
        }
        if (descriptor.InspectionFormBindings.Count > 0)
        {
            dropped.Add("inspectionFormBindings");
        }
        return dropped;
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
