namespace Harborline.Api.Foundation.Forms.Models;

/// <summary>
/// How a <see cref="ReusableUnitRef"/> selects which immutable version of a unit resolves
/// at the reference site — the seam that makes reuse a <b>reference, not a copy</b>
/// (D4, ADR 0135 amendment 2026-07-01).
/// </summary>
/// <remarks>
/// <para>
/// <b>Latest-published (the propagation channel).</b> When <see cref="PinnedVersion"/> is
/// <see langword="null"/>, the reference resolves to the highest
/// <see cref="FormDefinitionStatus.Published"/> version of the unit <em>at resolution time</em>.
/// Because the referencing definition stores only the reference — never an expanded copy —
/// publishing a new unit version <em>propagates</em> to every latest-published referencer on
/// the next resolve. This is the "editing the unit's new version propagates to referencers"
/// guarantee.
/// </para>
/// <para>
/// <b>Pinned.</b> When <see cref="PinnedVersion"/> is set, the reference resolves to exactly
/// that immutable version and does not drift when a newer version publishes. Still
/// reuse-by-reference (the content is fetched live from the unit store, never embedded) — a
/// pin freezes <em>which</em> version, not a snapshot of its content.
/// </para>
/// </remarks>
/// <param name="PinnedVersion">The exact version to resolve, or <see langword="null"/> to
/// track the latest published version.</param>
public readonly record struct ReusableUnitVersionSelector(SemanticVersion? PinnedVersion)
{
    /// <summary>Tracks the highest published version at resolution time (propagation channel).</summary>
    public static ReusableUnitVersionSelector LatestPublished => new((SemanticVersion?)null);

    /// <summary>Pins to an exact immutable version (does not drift on a newer publish).</summary>
    public static ReusableUnitVersionSelector Pin(SemanticVersion version) => new(version);

    /// <summary>True when this selector tracks the latest published version.</summary>
    public bool IsLatestPublished => PinnedVersion is null;
}

/// <summary>
/// A reference from a consuming definition to a <see cref="ReusableUnit"/> by
/// <c>(UnitId, Version-selector)</c> — <b>reuse-by-reference, not copy</b> (D4).
/// </summary>
/// <remarks>
/// Carried on a <see cref="FormItemKind.Reference"/> node. Phase 1 carries <em>no</em>
/// per-property override payload: every property of the referenced unit is CP-locked at the
/// reference site (the per-unit AP-override endpoint is deferred behind three named
/// kill-triggers). The absence of an override channel here is the enforcement — there is
/// structurally no way for a consumer to weaken a unit-provided property in this phase.
/// </remarks>
/// <param name="UnitId">The referenced unit's id.</param>
/// <param name="Version">Which version resolves (pinned, or latest-published).</param>
public sealed record ReusableUnitRef(
    ReusableUnitId UnitId,
    ReusableUnitVersionSelector Version);
