using System.Text;
using System.Text.Json.Nodes;

using Harborline.Api.Foundation.Blobs;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Merge;
using Harborline.Api.Foundation.Packs.Model;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

/// <summary>
/// T-655 (ADR 0096; supersedes T-536 / DES-0018 F10) — the raise-only safety-floor rule has ONE producer,
/// the platform's <c>PackageSafetyFloorReattachment</c>, and the api consumes it. A prior overlay that
/// lowers a floor, removes its key or removes the whole <c>safetyFloors</c> object still clamps back to the
/// seed and surfaces a <see cref="PackReattachConflictKind.FloorClamped"/>. A PRESENT MALFORMED
/// (non-integer) member is no longer clamped: it refuses the whole re-attach with the platform's
/// <c>platform-package-safety-floor-malformed</c> code, naming the member. The api's own
/// <c>ClampSafetyFloors</c> copy, which disagreed with the released contract on exactly that case, is gone.
/// </summary>
public sealed class PackReattachSafetyFloorClampTests
{
    private const string Key = "catalog";
    private const string Seed = """{"name":"c","safetyFloors":{"scoring":3}}""";

    [Fact(DisplayName = "T-655: a lower integer member clamps back to the seed floor")]
    public void Reattach_LowerInteger_ClampsToSeed()
        => AssertClamped("""{"safetyFloors":{"scoring":1}}""", "/safetyFloors/scoring", "1");

    [Fact(DisplayName = "T-655: a missing floor key is restored to the seed floor")]
    public void Reattach_MissingKey_RestoresSeed()
        => AssertClamped("""{"safetyFloors":{"scoring":null}}""", "/safetyFloors/scoring", "null");

    [Fact(DisplayName = "T-655: a removed safetyFloors object is restored to the seed's")]
    public void Reattach_RemovedObject_RestoresSeed()
        => AssertClamped("""{"safetyFloors":null}""", "safetyFloors", "null");

    [Fact(DisplayName = "T-655: a present string member refuses the re-attach and names the member")]
    public void Reattach_PresentString_RefusesNamingTheMember()
    {
        var plan = Plan("""{"safetyFloors":{"scoring":"1"}}""");

        Assert.Equal("platform-package-safety-floor-malformed", plan.RefusalCode);
        Assert.Equal("scoring", plan.RefusalMember);

        // A refusal is whole-re-attach: nothing carries forward and nothing is offered as a clamp.
        Assert.Empty(plan.Reattached);
        Assert.Empty(plan.Conflicts);
    }

    private static void AssertClamped(string overlayPatch, string expectedPath, string expectedAttempted)
    {
        var plan = Plan(overlayPatch);

        Assert.Null(plan.RefusalCode);

        // The clamp restored the seed exactly, so nothing weakened re-attaches ...
        Assert.Empty(plan.Reattached);

        // ... and the attempt is surfaced, naming the member and the value the overlay tried to keep.
        var conflict = Assert.Single(plan.Conflicts);
        Assert.Equal(PackReattachConflictKind.FloorClamped, conflict.Kind);
        Assert.Equal(expectedPath, conflict.Path);
        Assert.Equal(expectedAttempted, conflict.OverlayValueJson);
    }

    private static PackReattachPlan Plan(string overlayPatch)
        => PackReattachPlanner.Plan(
            priorSeeds: [new PackSeedItem(Key, PackContentKind.StandardsCatalog, "1.0.0", Seed, Cid.FromBytes([]))],
            newContents: [new PackContentItem(Key, PackContentKind.StandardsCatalog, "1.1.0", Encoding.UTF8.GetBytes(Seed), Cid.FromBytes([]))],
            priorOverrides: [new PackTenantOverride(Key, JsonNode.Parse(overlayPatch)!)],
            renamedFrom: null);
}
