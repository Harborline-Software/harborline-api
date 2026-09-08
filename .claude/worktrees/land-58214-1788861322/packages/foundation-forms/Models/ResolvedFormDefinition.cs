namespace Harborline.Api.Foundation.Forms.Models;

/// <summary>
/// The output of <see cref="IReuseResolver"/> — a <see cref="FormDefinition"/> whose
/// <see cref="FormItemKind.Reference"/> nodes have been expanded in place against the
/// reusable-unit store, plus the per-field reuse provenance (ADR 0129 D8).
/// </summary>
/// <remarks>
/// <para>
/// <b>Effective, not stored.</b> <see cref="Effective"/> is a resolution-time projection:
/// each reference node is replaced by a <see cref="FormItemKind.Group"/> carrying the
/// referenced unit's item subtree, and the unit's field overlays are merged (CP-locked)
/// into <see cref="HarborlineOverlay.Fields"/>. The <em>stored</em> definition keeps only the
/// reference — re-resolving after a unit's new version publishes yields updated content for
/// latest-published references (reuse-by-reference, not a frozen copy).
/// </para>
/// <para>
/// <b>Provenance.</b> <see cref="FieldProvenance"/> records, per reused field key, which
/// unit + version provided it and that it is CP-locked. A definition with no references
/// resolves to itself with an empty provenance map.
/// </para>
/// </remarks>
/// <param name="Effective">The reference-expanded definition (reused fields merged into the
/// overlay; reference nodes replaced by groups).</param>
/// <param name="FieldProvenance">Per-field reuse provenance, keyed by field name. Contains an
/// entry for every field contributed by a referenced unit; own fields are absent.</param>
public sealed record ResolvedFormDefinition(
    FormDefinition Effective,
    IReadOnlyDictionary<string, ReuseProvenance> FieldProvenance);
