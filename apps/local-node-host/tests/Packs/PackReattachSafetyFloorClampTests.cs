using System.Text;
using System.Text.Json.Nodes;

using Harborline.Api.Foundation.Blobs;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Merge;
using Harborline.Api.Foundation.Packs.Model;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

/// <summary>
/// T-536 (DES-0018 F10) — the S-10 re-attach clamp is raise-only for EVERY seeded safety floor. A prior
/// overlay that lowers a floor, removes its key, removes the whole <c>safetyFloors</c> object, or leaves a
/// present MALFORMED (non-integer) member in its place must leave the seed floor intact and surface a
/// <see cref="PackReattachConflictKind.FloorClamped"/> naming the member. Before the fix the malformed
/// member matched neither the lower-integer nor the missing-key branch, survived the merge, and was then
/// silently dropped by <see cref="PackSafetyFloors.Extract"/> — the floor vanished.
/// </summary>
public sealed class PackReattachSafetyFloorClampTests
{
    private const string Key = "catalog";
    private const string Seed = """{"name":"c","safetyFloors":{"scoring":3}}""";

    [Fact(DisplayName = "T-536: a lower integer member clamps back to the seed floor")]
    public void Reattach_LowerInteger_ClampsToSeed()
        => AssertClamped("""{"safetyFloors":{"scoring":1}}""", "/safetyFloors/scoring", "1");

    [Fact(DisplayName = "T-536: a missing floor key is restored to the seed floor")]
    public void Reattach_MissingKey_RestoresSeed()
        => AssertClamped("""{"safetyFloors":{"scoring":null}}""", "/safetyFloors/scoring", "null");

    [Fact(DisplayName = "T-536: a removed safetyFloors object is restored to the seed's")]
    public void Reattach_RemovedObject_RestoresSeed()
        => AssertClamped("""{"safetyFloors":null}""", "safetyFloors", "null");

    [Fact(DisplayName = "T-536: a present string member is a clamp, not a pass")]
    public void Reattach_PresentString_ClampsToSeed()
        => AssertClamped("""{"safetyFloors":{"scoring":"1"}}""", "/safetyFloors/scoring", "\"1\"");

    private static void AssertClamped(string overlayPatch, string expectedPath, string expectedAttempted)
    {
        var plan = PackReattachPlanner.Plan(
            priorSeeds: [new PackSeedItem(Key, PackContentKind.StandardsCatalog, "1.0.0", Seed, Cid.FromBytes([]))],
            newContents: [new PackContentItem(Key, PackContentKind.StandardsCatalog, "1.1.0", Encoding.UTF8.GetBytes(Seed), Cid.FromBytes([]))],
            priorOverrides: [new PackTenantOverride(Key, JsonNode.Parse(overlayPatch)!)],
            renamedFrom: null);

        // The clamp restored the seed exactly, so nothing weakened re-attaches ...
        Assert.Empty(plan.Reattached);

        // ... and the attempt is surfaced, naming the member and the value the overlay tried to keep.
        var conflict = Assert.Single(plan.Conflicts);
        Assert.Equal(PackReattachConflictKind.FloorClamped, conflict.Kind);
        Assert.Equal(expectedPath, conflict.Path);
        Assert.Equal(expectedAttempted, conflict.OverlayValueJson);
    }
}
