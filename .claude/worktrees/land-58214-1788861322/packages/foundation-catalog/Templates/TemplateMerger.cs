using System.Text.Json.Nodes;

namespace Harborline.Api.Foundation.Catalog.Templates;

/// <summary>
/// Applies a tenant overlay to a base template using JSON Merge Patch
/// (RFC 7396). Does not mutate its inputs.
/// </summary>
public static class TemplateMerger
{
    /// <summary>
    /// Resolves a base template and an overlay into a merged
    /// <see cref="TemplateDefinition"/>. Throws if the overlay's
    /// <see cref="TenantTemplateOverlay.BaseRef"/> does not match the base
    /// template's id or <c>id@version</c>.
    /// </summary>
    public static TemplateDefinition Resolve(TemplateDefinition baseDefinition, TenantTemplateOverlay overlay)
    {
        ArgumentNullException.ThrowIfNull(baseDefinition);
        ArgumentNullException.ThrowIfNull(overlay);

        var versioned = $"{baseDefinition.Id}@{baseDefinition.Version}";
        if (!string.Equals(overlay.BaseRef, baseDefinition.Id, StringComparison.Ordinal)
            && !string.Equals(overlay.BaseRef, versioned, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Overlay base ref '{overlay.BaseRef}' does not match base template '{versioned}'.");
        }

        var data = overlay.DataSchemaPatch is null
            ? baseDefinition.DataSchema.DeepClone()
            : ApplyMergePatch(baseDefinition.DataSchema, overlay.DataSchemaPatch)
                ?? throw new InvalidOperationException("Data schema patch produced a null result.");

        var ui = overlay.UiSchemaPatch is null
            ? baseDefinition.UiSchema.DeepClone()
            : ApplyMergePatch(baseDefinition.UiSchema, overlay.UiSchemaPatch)
                ?? throw new InvalidOperationException("UI schema patch produced a null result.");

        return baseDefinition with { DataSchema = data, UiSchema = ui };
    }

    /// <summary>
    /// Applies a JSON Merge Patch to a target document and returns a fresh
    /// result tree. Inputs are not mutated. Semantics follow RFC 7396:
    /// object-in-object patches merge recursively, null values in a patch
    /// object remove the corresponding target key, and any non-object patch
    /// replaces the target wholesale.
    /// </summary>
    public static JsonNode? ApplyMergePatch(JsonNode? target, JsonNode? patch)
    {
        if (patch is not JsonObject patchObject)
        {
            return patch?.DeepClone();
        }

        var result = new JsonObject();
        if (target is JsonObject targetObject)
        {
            foreach (var kvp in targetObject)
            {
                result[kvp.Key] = kvp.Value?.DeepClone();
            }
        }

        foreach (var kvp in patchObject)
        {
            if (kvp.Value is null)
            {
                result.Remove(kvp.Key);
            }
            else
            {
                result[kvp.Key] = ApplyMergePatch(result[kvp.Key], kvp.Value);
            }
        }

        return result;
    }

    /// <summary>
    /// Computes the RFC-7396 JSON-Merge-Patch that transforms <paramref name="from"/> into
    /// <paramref name="to"/> (the inverse of <see cref="ApplyMergePatch"/>). Returns <c>null</c> when the
    /// documents are already equal (an empty / no-op patch). Object keys present in <paramref name="from"/>
    /// but absent in <paramref name="to"/> become explicit <c>null</c> removals; changed/added keys carry
    /// their new value; unchanged keys are omitted. Arrays are atomic (replaced wholesale). Inputs are
    /// never mutated.
    /// </summary>
    public static JsonNode? ComputeMergePatch(JsonNode? from, JsonNode? to)
    {
        if (from is JsonObject fromObject && to is JsonObject toObject)
        {
            var patch = new JsonObject();
            foreach (var (key, _) in fromObject)
            {
                if (!toObject.ContainsKey(key))
                {
                    patch[key] = null; // removal
                }
            }

            foreach (var (key, toValue) in toObject)
            {
                if (!fromObject.ContainsKey(key))
                {
                    patch[key] = toValue?.DeepClone();
                }
                else
                {
                    var sub = ComputeMergePatch(fromObject[key], toValue);
                    if (sub is not null)
                    {
                        patch[key] = sub;
                    }
                }
            }

            return patch.Count == 0 ? null : patch;
        }

        return JsonNode.DeepEquals(from, to) ? null : to?.DeepClone();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  THREE-WAY overlay merge (ADR 0011 follow-up #1 — the upgrade re-attach the
    //  Pack Composer install engine (B-1b) rides on; architect fold A2). The
    //  two-way <see cref="Resolve"/> above applies a tenant overlay onto a single
    //  base; three-way re-attaches a tenant overlay authored over baseOld ONTO
    //  baseNew, flagging a conflict wherever BOTH the pack (baseOld→baseNew) and
    //  the tenant overlay touched the same JSON path (git-style, on RFC-7396
    //  JSON-Merge-Patch documents).
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Re-attaches a tenant overlay (an RFC-7396 patch authored against <paramref name="baseOld"/>)
    /// onto <paramref name="baseNew"/>, returning the merged document plus the conflicts. A path the
    /// pack CHANGED between versions AND the overlay also changed is a
    /// <see cref="ThreeWayConflictKind.ModifiedOnBothSides"/> conflict (the pack's new value wins in the
    /// merged doc; the overlay's intended value is reported). A path the overlay targets that the pack
    /// REMOVED in <paramref name="baseNew"/> is a <see cref="ThreeWayConflictKind.OrphanedByRemoval"/>
    /// conflict (S-10 — never a silent drop). Where only the overlay changed a path, the overlay
    /// re-attaches cleanly. Inputs are never mutated.
    /// </summary>
    public static ThreeWayMergeResult ThreeWayMerge(JsonNode? baseOld, JsonNode? baseNew, JsonNode? overlayPatch)
    {
        var conflicts = new List<ThreeWayMergeConflict>();

        // A non-object overlay patch replaces the whole document (RFC 7396). If the pack ALSO changed
        // the document wholesale, that is a both-sides conflict; otherwise the overlay re-attaches.
        if (overlayPatch is not JsonObject patchObject)
        {
            if (!JsonNode.DeepEquals(baseOld, baseNew))
            {
                conflicts.Add(new ThreeWayMergeConflict(
                    string.Empty, ThreeWayConflictKind.ModifiedOnBothSides,
                    baseNew?.DeepClone(), overlayPatch?.DeepClone()));
                return new ThreeWayMergeResult(baseNew?.DeepClone() ?? new JsonObject(), conflicts);
            }

            return new ThreeWayMergeResult(overlayPatch?.DeepClone() ?? new JsonObject(), conflicts);
        }

        var merged = baseNew is JsonObject ? baseNew.DeepClone() : new JsonObject();
        MergeObject((JsonObject)merged, baseOld as JsonObject, baseNew as JsonObject, patchObject, string.Empty, conflicts);
        return new ThreeWayMergeResult(merged, conflicts);
    }

    private static void MergeObject(
        JsonObject merged,
        JsonObject? oldObject,
        JsonObject? newObject,
        JsonObject patchObject,
        string path,
        List<ThreeWayMergeConflict> conflicts)
    {
        foreach (var (key, patchValue) in patchObject)
        {
            var childPath = $"{path}/{key}";
            var oldChild = oldObject? [key];
            var newChild = newObject? [key];
            var newHasKey = newObject is not null && newObject.ContainsKey(key);

            // Nested object patch onto a nested object in baseNew ⇒ recurse (per-field granularity).
            if (patchValue is JsonObject nestedPatch && newChild is JsonObject)
            {
                MergeObject((JsonObject)merged[key]!, oldChild as JsonObject, (JsonObject)newChild, nestedPatch, childPath, conflicts);
                continue;
            }

            // Leaf decision. The overlay's INTENDED effect at this path:
            //   patchValue == null  ⇒ delete the key; else replace (object-patch onto a non-object base
            //   collapses to a merge-patch apply, RFC 7396).
            var intendedRemove = patchValue is null;
            var intendedValue = intendedRemove ? null : ApplyMergePatch(oldChild, patchValue);

            // Does the overlay actually differ from the new pack value? (A no-op override needs no work.)
            var overrideIsNoOp = intendedRemove ? !newHasKey : JsonNode.DeepEquals(newChild, intendedValue);
            if (overrideIsNoOp)
            {
                continue;
            }

            // The pack REMOVED the key the override targets ⇒ orphaned (S-10 — surface, never silently drop).
            if (!newHasKey)
            {
                conflicts.Add(new ThreeWayMergeConflict(
                    childPath, ThreeWayConflictKind.OrphanedByRemoval, null, intendedValue?.DeepClone()));
                continue;
            }

            // The pack CHANGED this path between versions AND the overlay changed it ⇒ both-sides conflict:
            // keep the pack's new value in the merged doc, report the overlay's intended value.
            if (!JsonNode.DeepEquals(oldChild, newChild))
            {
                conflicts.Add(new ThreeWayMergeConflict(
                    childPath, ThreeWayConflictKind.ModifiedOnBothSides, newChild?.DeepClone(), intendedValue?.DeepClone()));
                continue;
            }

            // Only the overlay changed this path ⇒ re-attach it cleanly onto baseNew.
            if (intendedRemove)
            {
                merged.Remove(key);
            }
            else
            {
                merged[key] = intendedValue;
            }
        }
    }
}

/// <summary>The kind of a three-way merge conflict (<see cref="TemplateMerger.ThreeWayMerge"/>).</summary>
public enum ThreeWayConflictKind
{
    /// <summary>Both the pack (baseOld→baseNew) and the tenant overlay changed the same path — the
    /// pack's new value wins in the merged document; the overlay's intended value is reported so the
    /// install-preview can surface it (ADR 0129 D8 errors-vs-choices).</summary>
    ModifiedOnBothSides = 0,

    /// <summary>The tenant overlay targets a path the pack REMOVED in baseNew — the override cannot
    /// re-attach and is surfaced as ORPHANED (S-10 — a rename/removal never silently reverts an
    /// override to pack behaviour).</summary>
    OrphanedByRemoval = 1,
}

/// <summary>
/// One conflict from a <see cref="TemplateMerger.ThreeWayMerge"/> — the JSON <see cref="Path"/>, the
/// <see cref="Kind"/>, the value the pack now carries at that path (<see cref="BaseNewValue"/>, null
/// when removed), and the value the tenant overlay intended (<see cref="OverlayValue"/>).
/// </summary>
/// <param name="Path">A leading-slash JSON path to the conflicting key (empty ⇒ whole-document).</param>
/// <param name="Kind">The conflict kind.</param>
/// <param name="BaseNewValue">The pack's value at the path in baseNew (null when the pack removed it).</param>
/// <param name="OverlayValue">The tenant overlay's intended value at the path (null when the overlay
/// intended a deletion).</param>
public sealed record ThreeWayMergeConflict(
    string Path,
    ThreeWayConflictKind Kind,
    JsonNode? BaseNewValue,
    JsonNode? OverlayValue);

/// <summary>
/// The result of a <see cref="TemplateMerger.ThreeWayMerge"/> — the <see cref="Merged"/> document
/// (baseNew with the non-conflicting overlay re-attached) plus the <see cref="Conflicts"/>. An empty
/// conflict list means every override re-attached cleanly.
/// </summary>
/// <param name="Merged">The re-attached document.</param>
/// <param name="Conflicts">The surfaced conflicts (empty ⇒ clean re-attach).</param>
public sealed record ThreeWayMergeResult(JsonNode Merged, IReadOnlyList<ThreeWayMergeConflict> Conflicts);
