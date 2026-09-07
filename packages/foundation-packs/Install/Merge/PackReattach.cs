using System.Text;
using System.Text.Json.Nodes;

using Harborline.Api.Foundation.Catalog.Templates;
using Harborline.Api.Foundation.Packs.Model;

namespace Harborline.Api.Foundation.Packs.Install.Merge;

/// <summary>The kind of a re-attach outcome surfaced in the conflict report (ADR 0129 D8 vocabulary).</summary>
public enum PackReattachConflictKind
{
    /// <summary>Both the pack (old→new version) and the tenant overlay changed the same path — the pack's
    /// new value wins; the overlay's intended value is surfaced for the admin to re-affirm.</summary>
    ModifiedOnBothSides = 0,

    /// <summary>The tenant overlay targeted a content key / path the new version REMOVED — it cannot
    /// re-attach and is surfaced as ORPHANED (S-10 — never a silent revert).</summary>
    OrphanedByRemoval = 1,

    /// <summary>A StandardsCatalog safety floor the overlay tried to LOWER below the seed's floor — the
    /// merge CLAMPED it back up (raise-strictness-only, F1/S-4) and surfaces the attempt.</summary>
    FloorClamped = 2,
}

/// <summary>
/// One re-attach conflict — the content key + kind + the specific JSON path, plus the seed's value and
/// the overlay's intended value (both as JSON text, for the D8 install-preview). A single override can
/// re-attach on some paths AND surface conflicts on others (partial re-attach), so this is per-path.
/// </summary>
/// <param name="ContentKey">The seed content key the override targeted.</param>
/// <param name="ContentKind">The declarative kind of the content.</param>
/// <param name="Kind">The conflict kind.</param>
/// <param name="Path">The JSON path within the content (empty ⇒ whole-content, e.g. orphaned key).</param>
/// <param name="SeedValueJson">The pack's value at the path (null when removed).</param>
/// <param name="OverlayValueJson">The tenant overlay's intended value at the path (null when a deletion).</param>
public sealed record PackReattachConflict(
    string ContentKey,
    PackContentKind ContentKind,
    PackReattachConflictKind Kind,
    string Path,
    string? SeedValueJson,
    string? OverlayValueJson);

/// <summary>
/// The plan for re-attaching a prior version's tenant overrides onto a new version's seed (S-10 total
/// re-attach). <see cref="Reattached"/> is the set of overrides that carry forward (re-keyed + re-based
/// onto the new seed); <see cref="Conflicts"/> is everything that did NOT cleanly re-attach. Their union
/// accounts for EVERY prior override path — nothing is silently dropped.
/// </summary>
/// <param name="Reattached">Overrides re-expressed as patches over the new seed content.</param>
/// <param name="Conflicts">Modified-both-sides / orphaned / floor-clamped surfaces (D8).</param>
public sealed record PackReattachPlan(
    IReadOnlyList<PackTenantOverride> Reattached,
    IReadOnlyList<PackReattachConflict> Conflicts);

/// <summary>
/// Re-attaches a prior installed version's tenant overrides onto a new version's seed layer via the
/// ADR 0011 three-way merge (<see cref="TemplateMerger.ThreeWayMerge"/>, fold A2), driven by a
/// PER-CONTENT-TYPE merge table. Catalog scoring floors use the raise-strictness CLAMP (never a plain
/// JSON merge that could lower a floor — F1/S-4/S-8); a renamed content key re-attaches via the manifest
/// <see cref="PackContentRename"/> map (S-10); a removed key surfaces as an orphaned conflict.
/// </summary>
public static class PackReattachPlanner
{
    /// <summary>
    /// Plans re-attach of <paramref name="priorOverrides"/> (authored over <paramref name="priorSeeds"/>)
    /// onto <paramref name="newContents"/>, honouring the <paramref name="renamedFrom"/> key-stability map.
    /// </summary>
    public static PackReattachPlan Plan(
        IReadOnlyList<PackSeedItem> priorSeeds,
        IReadOnlyList<PackContentItem> newContents,
        IReadOnlyList<PackTenantOverride> priorOverrides,
        IReadOnlyList<PackContentRename>? renamedFrom)
    {
        ArgumentNullException.ThrowIfNull(priorSeeds);
        ArgumentNullException.ThrowIfNull(newContents);
        ArgumentNullException.ThrowIfNull(priorOverrides);

        var seedByKey = priorSeeds.ToDictionary(s => s.Key, StringComparer.Ordinal);
        var newByKey = newContents.ToDictionary(c => c.Key, StringComparer.Ordinal);

        // renamedFrom is (newKey ← oldKey); index by the OLD key so an override on the old key finds the new.
        var oldToNew = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rename in renamedFrom ?? Array.Empty<PackContentRename>())
        {
            oldToNew[rename.OldKey] = rename.NewKey;
        }

        var reattached = new List<PackTenantOverride>();
        var conflicts = new List<PackReattachConflict>();

        foreach (var over in priorOverrides)
        {
            var oldKey = over.ContentKey;
            var newKey = oldToNew.GetValueOrDefault(oldKey, oldKey);

            if (!newByKey.TryGetValue(newKey, out var newItem))
            {
                // The new version dropped (or renamed-away without a map entry) the key the override
                // targets ⇒ ORPHANED (S-10 — surfaced with its intended value, never silently dropped).
                conflicts.Add(new PackReattachConflict(
                    oldKey, ClassifyKindOf(seedByKey, oldKey), PackReattachConflictKind.OrphanedByRemoval,
                    Path: string.Empty, SeedValueJson: null,
                    OverlayValueJson: over.OverlayPatch.ToJsonString()));
                continue;
            }

            var baseOld = seedByKey.TryGetValue(oldKey, out var priorSeed)
                ? priorSeed.ParseContent()
                : new JsonObject();
            var baseNew = ParseUtf8(newItem.CanonicalBytes);

            var merge = TemplateMerger.ThreeWayMerge(baseOld, baseNew, over.OverlayPatch);
            var merged = merge.Merged;

            foreach (var c in merge.Conflicts)
            {
                conflicts.Add(new PackReattachConflict(
                    newKey, newItem.Kind, Map(c.Kind), c.Path,
                    c.BaseNewValue?.ToJsonString(), c.OverlayValue?.ToJsonString()));
            }

            // PER-CONTENT-TYPE merge table: StandardsCatalog floors are raise-strictness-only. Clamp any
            // floor the overlay tried to lower below the seed's floor back up (F1/S-4) + surface it.
            if (newItem.Kind == PackContentKind.StandardsCatalog)
            {
                ClampSafetyFloors(newKey, baseNew, merged, conflicts);
            }

            // Re-express the re-attached override as a patch over the NEW seed (so the next upgrade can
            // three-way it again). A no-op patch (the override fully conflicted away) re-attaches nothing.
            var reattachPatch = TemplateMerger.ComputeMergePatch(baseNew, merged);
            if (reattachPatch is not null)
            {
                reattached.Add(new PackTenantOverride(newKey, reattachPatch));
            }
        }

        return new PackReattachPlan(reattached, conflicts);
    }

    /// <summary>
    /// Raise-strictness clamp for catalog safety floors (F1/S-4): for every floor the new SEED declares,
    /// the merged result must be ≥ the seed's floor. A merged floor below the seed's (the overlay tried to
    /// lower it) is clamped back up and surfaced as a <see cref="PackReattachConflictKind.FloorClamped"/>.
    /// </summary>
    private static void ClampSafetyFloors(
        string contentKey, JsonNode baseNew, JsonNode merged, List<PackReattachConflict> conflicts)
    {
        var seedFloors = PackSafetyFloors.Extract(baseNew);
        if (seedFloors.Count == 0 || merged is not JsonObject mergedObject)
        {
            return;
        }

        if (mergedObject[PackSafetyFloors.FloorsProperty] is not JsonObject mergedFloors)
        {
            // The overlay stripped the floors object entirely — restore the seed's floors verbatim.
            if (baseNew is JsonObject bn && bn[PackSafetyFloors.FloorsProperty] is JsonObject seedFloorObj)
            {
                mergedObject[PackSafetyFloors.FloorsProperty] = seedFloorObj.DeepClone();
                conflicts.Add(new PackReattachConflict(
                    contentKey, PackContentKind.StandardsCatalog, PackReattachConflictKind.FloorClamped,
                    PackSafetyFloors.FloorsProperty, seedFloorObj.ToJsonString(), "null"));
            }

            return;
        }

        foreach (var (floorKey, seedStrictness) in seedFloors)
        {
            var mergedIsBelow = mergedFloors[floorKey] is JsonValue mv
                && mv.TryGetValue(out int mergedStrictness)
                && mergedStrictness < seedStrictness;
            var missing = !mergedFloors.ContainsKey(floorKey);

            if (mergedIsBelow || missing)
            {
                var attempted = mergedFloors[floorKey]?.ToJsonString() ?? "null";
                mergedFloors[floorKey] = seedStrictness; // clamp up to the seed floor (raise-only)
                conflicts.Add(new PackReattachConflict(
                    contentKey, PackContentKind.StandardsCatalog, PackReattachConflictKind.FloorClamped,
                    $"/{PackSafetyFloors.FloorsProperty}/{floorKey}", seedStrictness.ToString(), attempted));
            }
        }
    }

    private static PackContentKind ClassifyKindOf(IReadOnlyDictionary<string, PackSeedItem> seedByKey, string key)
        => seedByKey.TryGetValue(key, out var s) ? s.Kind : PackContentKind.FormDefinition;

    private static PackReattachConflictKind Map(ThreeWayConflictKind kind) => kind switch
    {
        ThreeWayConflictKind.OrphanedByRemoval => PackReattachConflictKind.OrphanedByRemoval,
        _ => PackReattachConflictKind.ModifiedOnBothSides,
    };

    private static JsonNode ParseUtf8(ReadOnlyMemory<byte> canonicalBytes)
        => JsonNode.Parse(Encoding.UTF8.GetString(canonicalBytes.Span))
           ?? throw new InvalidOperationException("Pack content canonical bytes parsed to a null JSON document.");
}
