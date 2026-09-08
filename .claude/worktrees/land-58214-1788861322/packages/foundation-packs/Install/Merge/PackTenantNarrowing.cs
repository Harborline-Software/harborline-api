using System.Text.Json.Nodes;

namespace Harborline.Api.Foundation.Packs.Install.Merge;

/// <summary>
/// The one rule an administrator's tenant overlay must satisfy: it may only NARROW the seed item it is
/// authored over (ticket 208 L624). A tenant override is the ORDINARY overlay pipeline — an RFC-7396
/// JSON-Merge-Patch stored per (tenant, pack key, content key), re-attached across an upgrade by
/// <see cref="PackReattachPlanner"/> — and this is the admission on its write door, not a second pipeline.
/// </summary>
/// <remarks>
/// <para>
/// A narrowing is expressible in RFC-7396 in exactly three shapes, and nothing else is a narrowing:
/// </para>
/// <list type="bullet">
///   <item>a <c>null</c> at a key the seed declares — the removal of a form field, a workflow state, a
///   trigger;</item>
///   <item>a nested object over a nested object the seed declares — recursion toward one of the other
///   two shapes;</item>
///   <item>an array that is a PROPER SUBSET (by value) of the seed's array at the same path — a pack
///   binding's offered roles reduced, a field's option list reduced. RFC-7396 has no array-element
///   patch, so a reduced list is written whole; membership is what makes it a narrowing.</item>
/// </list>
/// <para>
/// Everything else WIDENS and is refused with <see cref="WideningRefusedCode"/>: a key the seed does not
/// declare (a new field, a new state), a scalar replaced by another scalar (a label, a limit — the seed's
/// value is the reviewed one), an array that adds a member (a role the publisher never offered), or a
/// non-object patch at the root (a wholesale replacement of the reviewed content).
/// </para>
/// <para>
/// The BINDING kind carries a second, independent ceiling that this rule does not duplicate: an offered
/// role set reaching the store passes <c>AuthorizationDefinitionAdmission</c>, whose slice-3 rule already
/// refuses any pack-published definition offering a role outside the platform seed's reviewed offer. A
/// narrowed binding is therefore bounded twice — by subset here, and by the publisher ceiling there.
/// </para>
/// </remarks>
public static class PackTenantNarrowing
{
    /// <summary>A tenant overlay that would WIDEN the seed definition rather than narrow it.</summary>
    public const string WideningRefusedCode = "pack.overlay.widens_definition";

    /// <summary>
    /// True when <paramref name="overlayPatch"/> only narrows <paramref name="seed"/>.
    /// </summary>
    /// <param name="seed">The reviewed seed item content the overlay is authored over.</param>
    /// <param name="overlayPatch">The tenant's RFC-7396 patch.</param>
    /// <param name="wideningPath">The first path that widens (empty ⇒ the whole document).</param>
    public static bool IsNarrowing(JsonNode? seed, JsonNode? overlayPatch, out string wideningPath)
    {
        wideningPath = string.Empty;

        // A non-object patch at the root replaces the reviewed document wholesale (RFC 7396) — never a
        // narrowing, whatever it happens to contain.
        if (overlayPatch is not JsonObject patch || seed is not JsonObject seedObject)
        {
            return false;
        }

        return NarrowsObject(seedObject, patch, string.Empty, ref wideningPath);
    }

    private static bool NarrowsObject(
        JsonObject seed, JsonObject patch, string path, ref string wideningPath)
    {
        foreach (var (key, patchValue) in patch)
        {
            var childPath = $"{path}/{key}";
            if (!seed.TryGetPropertyValue(key, out var seedValue))
            {
                // The overlay declares a key the reviewed seed does not: an added field / state / rule.
                wideningPath = childPath;
                return false;
            }

            switch (patchValue)
            {
                // Removal — the canonical narrowing.
                case null:
                    continue;

                case JsonObject nested when seedValue is JsonObject nestedSeed:
                    if (!NarrowsObject(nestedSeed, nested, childPath, ref wideningPath))
                    {
                        return false;
                    }

                    continue;

                case JsonArray reduced when seedValue is JsonArray seedArray:
                    if (IsProperSubset(reduced, seedArray))
                    {
                        continue;
                    }

                    wideningPath = childPath;
                    return false;

                default:
                    // A scalar (or a shape change) replacing the reviewed value.
                    wideningPath = childPath;
                    return false;
            }
        }

        return true;
    }

    /// <summary>Every member of <paramref name="reduced"/> appears in <paramref name="seedArray"/> by
    /// VALUE, and the list is strictly shorter — a re-ordering or a re-statement is not a narrowing.</summary>
    private static bool IsProperSubset(JsonArray reduced, JsonArray seedArray)
        => reduced.Count < seedArray.Count
           && reduced.All(member => seedArray.Any(seedMember => JsonNode.DeepEquals(seedMember, member)));
}
