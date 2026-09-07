namespace Harborline.Api.Blocks.Assets.Registry.Model.Scoring;

/// <summary>
/// The criticality of a scored item (annex §3.8, D-P/D-R). Ordered by increasing strictness so
/// the cascade resolver can enforce a <b>raise-strictness-only</b> rule on seed-declared floors
/// (F1): a downstream layer may raise criticality but never lower a pack/base-declared floor.
/// </summary>
/// <remarks>
/// The numeric order IS the strictness order — do not reorder members. A higher value is
/// stricter; the resolver compares by value.
/// </remarks>
public enum ScoringCriticality
{
    /// <summary>Ordinary item — contributes to the weighted composite; may be averaged.</summary>
    Normal = 0,

    /// <summary>
    /// Critical item — hard-fails the unit regardless of the composite and blocks the
    /// "above standard" band until cleared (never averaged away).
    /// </summary>
    Critical = 1,

    /// <summary>
    /// Life-safety critical — hard-fails and auto-raises a life-safety deficiency with a
    /// repair-deadline timer (Wave 3 evaluation). The strictest floor; cannot be lowered downstream.
    /// </summary>
    SafetyCritical = 2,
}
