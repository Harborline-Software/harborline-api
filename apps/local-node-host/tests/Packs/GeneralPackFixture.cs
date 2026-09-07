using System.Text.Json.Nodes;

using Harborline.Api.Blocks.Assets.Registry.Model;
using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.Foundation.Blobs;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.LocalNodeHost.Data.PackProjection;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

/// <summary>
/// The #127 hand-authored "General" starter pack, inlined for the Pack Composer B-2a acceptance. The six
/// <c>AssetTypeDefinition</c> content bodies mirror <c>_shared/packs/general/general-pack.export.json</c>
/// EXACTLY (including the incidental trait array order the hand-authored file carries) — the acceptance
/// case is: re-author THESE six in the Composer and produce a signed artifact that carries the SAME six
/// types (per-item content-address match under the ONE canonicalizer; NOT raw byte-identity, since a
/// trait-FLAGS value cannot recover a source array's order — see <see cref="PackAssetTypeContent.ToContent"/>).
/// </summary>
internal static class GeneralPackFixture
{
    /// <summary>The pack key + version the hand-authored fixture declares.</summary>
    public const string PackKey = "harborline.general";
    public const string PackVersion = "1.0.0";

    /// <summary>The six raw content bodies, fixture-exact (trait order as authored).</summary>
    public static IReadOnlyList<(string Key, JsonObject Content)> RawContents() => new (string, JsonObject)[]
    {
        ("general.equipment", Body("general.equipment", "Equipment", new[] { "Maintainable" }, 10, 5)),
        ("general.vehicle", Body("general.vehicle", "Vehicle", new[] { "Movable", "Maintainable" }, 8, 5)),
        ("general.furniture", Body("general.furniture", "Furniture", new[] { "Movable" }, 15, 5)),
        ("general.it-computer", Body("general.it-computer", "IT / Computer", new[] { "Movable", "Maintainable" }, 4, 5)),
        ("general.tool", Body("general.tool", "Tool", new[] { "Movable", "Maintainable" }, 7, 5)),
        ("general.facility", Body("general.facility", "Property / Facility", new[] { "Container" }, 40, 5)),
    };

    /// <summary>The six type ids (in fixture order).</summary>
    public static IReadOnlyList<string> TypeIds() => RawContents().Select(c => c.Key).ToList();

    /// <summary>Parses each raw content into its (id, descriptor) — the exact pair the hand-authored pack
    /// projects into the registry. Fails the test if any body is malformed.</summary>
    public static IReadOnlyList<(EntityTypeId Id, EntityTypeDescriptor Descriptor)> Descriptors()
    {
        var result = new List<(EntityTypeId, EntityTypeDescriptor)>();
        foreach (var (key, body) in RawContents())
        {
            Assert.True(
                PackAssetTypeContent.TryParse(body, out var id, out var descriptor, out var error),
                $"fixture '{key}' should parse: {error}");
            result.Add((id, descriptor));
        }
        return result;
    }

    /// <summary>Seeds a registry with the six types at <see cref="CascadeLayer.Pack"/> provenance — mirroring
    /// what installing + projecting the hand-authored pack does (the Composer then re-reads them at compose).</summary>
    public static void SeedInto(IEntityTypeRegistry registry)
    {
        foreach (var (id, descriptor) in Descriptors())
        {
            registry.SeedType(new EntityTypeSeed(id, descriptor, CascadeLayer.Pack));
        }
    }

    /// <summary>The CANONICAL content-address of a type — the byte-stable Cid of its canonical
    /// <c>AssetTypeDefinition</c> emit. Two types are equivalent iff this matches (the acceptance's
    /// "per-item content-address match"); it sidesteps <see cref="EntityTypeDescriptor"/> record equality,
    /// whose collection members compare by reference.</summary>
    public static string CanonicalCid(EntityTypeId id, EntityTypeDescriptor descriptor)
        => Cid.FromBytes(CanonicalJson.Serialize<JsonNode>(PackAssetTypeContent.ToContent(id, descriptor))).Value;

    /// <summary>The six types' expected canonical content-addresses, keyed by type id.</summary>
    public static IReadOnlyDictionary<string, string> ExpectedCanonicalCids()
        => Descriptors().ToDictionary(d => d.Id.Value, d => CanonicalCid(d.Id, d.Descriptor));

    private static JsonObject Body(string id, string displayName, string[] traits, int life, int scale)
    {
        var traitArr = new JsonArray();
        foreach (var t in traits) traitArr.Add((JsonNode)t);
        return new JsonObject
        {
            ["id"] = id,
            ["displayName"] = displayName,
            ["traits"] = traitArr,
            ["expectedUsefulLifeYears"] = life,
            ["conditionScaleMax"] = scale,
        };
    }
}
