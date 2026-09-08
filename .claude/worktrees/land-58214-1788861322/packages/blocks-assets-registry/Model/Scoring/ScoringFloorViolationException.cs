using Harborline.Api.Foundation.Definitions;

namespace Harborline.Api.Blocks.Assets.Registry.Model.Scoring;

/// <summary>
/// Thrown when a downstream cascade layer attempts to <b>lower</b> a seed-declared
/// (base/pack) critical or safety floor for a scored field (ADR 0101 Rev 3.1 /
/// security-engineering finding <b>F1</b>). Overrides may raise strictness only; a lower layer
/// can never soften a pack-declared life-safety floor. The violation is provenance-visible: the
/// message names the field, the seed layer that set the floor, and the layer that tried to lower
/// it.
/// </summary>
public sealed class ScoringFloorViolationException : Exception
{
    /// <summary>The field whose floor was violated.</summary>
    public string FieldPointer { get; }

    /// <summary>The criticality declared by the seed (base/pack) floor.</summary>
    public ScoringCriticality SeedFloor { get; }

    /// <summary>The cascade layer that declared the seed floor.</summary>
    public CascadeLayer SeedLayer { get; }

    /// <summary>The lower criticality the offending layer attempted to set.</summary>
    public ScoringCriticality AttemptedCriticality { get; }

    /// <summary>The cascade layer that attempted to lower the floor.</summary>
    public CascadeLayer AttemptedLayer { get; }

    /// <summary>Constructs the exception with full provenance.</summary>
    public ScoringFloorViolationException(
        string fieldPointer,
        ScoringCriticality seedFloor,
        CascadeLayer seedLayer,
        ScoringCriticality attemptedCriticality,
        CascadeLayer attemptedLayer)
        : base(
            $"Scoring floor violation on field '{fieldPointer}': the {seedLayer} seed declared "
            + $"criticality {seedFloor}, but the {attemptedLayer} layer attempted to lower it to "
            + $"{attemptedCriticality}. Seed-declared critical/safety floors are raise-strictness-only "
            + "(F1) — a lower layer may raise, never lower, a seeded floor.")
    {
        FieldPointer = fieldPointer;
        SeedFloor = seedFloor;
        SeedLayer = seedLayer;
        AttemptedCriticality = attemptedCriticality;
        AttemptedLayer = attemptedLayer;
    }
}
