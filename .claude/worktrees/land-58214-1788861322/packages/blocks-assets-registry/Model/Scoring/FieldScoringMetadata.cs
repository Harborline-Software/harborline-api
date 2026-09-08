using Harborline.Api.Foundation.Definitions;

namespace Harborline.Api.Blocks.Assets.Registry.Model.Scoring;

/// <summary>
/// Per-field scoring metadata (annex §3.8, D-P) — the payload that turns one field of a form
/// (a standard's item catalog) into a scored item.
/// </summary>
/// <remarks>
/// <para>
/// <b>A separate per-field layer (A2).</b> This is deliberately NOT a member of
/// <c>Harborline.Api.Foundation.Forms.Models.FieldOverlay</c> and does NOT extend the closed
/// eight-aspect ADR 0140 <c>AspectOverlay</c> governance keystone — it follows the
/// <i>shape precedent</i> of a per-field overlay while riding its own config layer, so a scoring
/// change never touches the governance keystone's back-compat guarantee or its publish-time
/// monotonic-tighten resolver.
/// </para>
/// <para>
/// <see cref="Criticality"/> carries the F1 floor semantics: a pack/base-declared critical or
/// safety floor is raise-strictness-only downstream (see
/// <see cref="ScoringCascadeResolver"/>). <see cref="Provenance"/> records which cascade layer
/// declared this metadata so any override is attributable.
/// </para>
/// </remarks>
/// <param name="FieldPointer">JSON-pointer-style path to the field this metadata scores.</param>
/// <param name="Weight">Weight in the parent category's roll-up (non-negative).</param>
/// <param name="Category">The roll-up category (a form section maps to a category).</param>
/// <param name="Discipline">The discipline tag, or <see cref="DisciplineTag.Generic"/>.</param>
/// <param name="Kind">The item kind (rating / measurement / safety pass-fail).</param>
/// <param name="Criticality">The item criticality (see F1 floor rule).</param>
/// <param name="ValidityPeriod">
/// How long an answer stays valid (D-S). An answer past its window goes stale and degrades the
/// composite honestly. Null = never expires.
/// </param>
/// <param name="MeasurementRange">The acceptable range, required semantics only for measurement kind.</param>
/// <param name="Provenance">The cascade layer that declared this metadata (attribution / floor origin).</param>
public sealed record FieldScoringMetadata(
    string FieldPointer,
    decimal Weight,
    string Category,
    DisciplineTag Discipline,
    ScoringItemKind Kind,
    ScoringCriticality Criticality,
    TimeSpan? ValidityPeriod = null,
    MeasurementRange? MeasurementRange = null,
    CascadeLayer Provenance = CascadeLayer.Base)
{
    /// <summary>Validates the invariants a single metadata entry must always hold.</summary>
    /// <exception cref="ArgumentException">Weight is negative or the field pointer is blank.</exception>
    public FieldScoringMetadata Validated()
    {
        if (string.IsNullOrWhiteSpace(FieldPointer))
        {
            throw new ArgumentException("FieldPointer must be a non-empty field path.", nameof(FieldPointer));
        }

        if (Weight < 0m)
        {
            throw new ArgumentException($"Weight must be non-negative (was {Weight}).", nameof(Weight));
        }

        return this;
    }
}
