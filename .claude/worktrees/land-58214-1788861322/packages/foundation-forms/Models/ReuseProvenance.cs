namespace Harborline.Api.Foundation.Forms.Models;

/// <summary>
/// The lock posture of a property resolved from a <see cref="ReusableUnit"/> at a reference
/// site (ADR 0129 D5/D8 — CP-locked vs AP-cascade). Phase 1 emits only
/// <see cref="CpLocked"/>; <see cref="ApOverridable"/> is reserved for the deferred per-unit
/// AP-override endpoint so the provenance contract does not change shape when it lands.
/// </summary>
public enum ReuseLock
{
    /// <summary>Locked at the reference site — the consuming definition cannot override it
    /// (the Phase-1 all-CP-locked endpoint of the cascade).</summary>
    CpLocked = 0,

    /// <summary>Overridable by the consumer via the (deferred) per-unit AP-override endpoint.
    /// Never emitted in Phase 1.</summary>
    ApOverridable = 1,
}

/// <summary>
/// Provenance for a property that a resolved definition inherited from a
/// <see cref="ReusableUnit"/> — the ADR 0129 D8 "shared primitive" that makes the cascade
/// legible ("which unit provided this + is it locked").
/// </summary>
/// <remarks>
/// Emitted by <see cref="IReuseResolver"/> into
/// <see cref="ResolvedFormDefinition.FieldProvenance"/>, keyed by field name. It is the
/// data contract a future authoring UI renders as the composition analogue of devtools
/// "computed styles" — a CP-locked knob is shown but inert, tagged with the owning unit.
/// </remarks>
/// <param name="UnitId">The unit that provided the property.</param>
/// <param name="UnitVersion">The exact resolved version of that unit (concrete even when
/// the reference tracked latest-published — this is the version that actually resolved).</param>
/// <param name="ReferenceKey">The reference-site key (the container key the unit's values
/// nest under) the property was contributed through.</param>
/// <param name="Lock">The lock posture — always <see cref="ReuseLock.CpLocked"/> in Phase 1.</param>
public sealed record ReuseProvenance(
    ReusableUnitId UnitId,
    SemanticVersion UnitVersion,
    string ReferenceKey,
    ReuseLock Lock);
