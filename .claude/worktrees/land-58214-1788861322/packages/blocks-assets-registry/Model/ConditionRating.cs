namespace Harborline.Api.Blocks.Assets.Registry.Model;

/// <summary>
/// A condition grade on a per-type configurable scale (annex §3.8, D-M) — generalized beyond the
/// equipment-only 4-value <c>Harborline.Api.Blocks.Inspections.ConditionRating</c> enum.
/// </summary>
/// <remarks>
/// The scale is configurable per type on the cascade; the default is a 5-point scale
/// (<see cref="DefaultScaleMax"/>). <see cref="Grade"/> is 1-based where 1 is worst and
/// <see cref="ScaleMax"/> is best; an optional <see cref="Label"/> carries the qualitative name
/// (e.g. "Good", "Poor"). This is a value object — no identity, compared by value.
/// </remarks>
public readonly record struct ConditionRating
{
    /// <summary>The default 5-point scale maximum.</summary>
    public const int DefaultScaleMax = 5;

    /// <summary>The rated grade (1 = worst … <see cref="ScaleMax"/> = best).</summary>
    public int Grade { get; }

    /// <summary>The maximum grade of the scale this rating was made on.</summary>
    public int ScaleMax { get; }

    /// <summary>Optional qualitative label for the grade.</summary>
    public string? Label { get; }

    /// <summary>
    /// Constructs a condition rating, validating <paramref name="grade"/> is within
    /// <c>[1, <paramref name="scaleMax"/>]</c> and the scale is at least 2 points.
    /// </summary>
    public ConditionRating(int grade, int scaleMax = DefaultScaleMax, string? label = null)
    {
        if (scaleMax < 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(scaleMax), scaleMax, "A condition scale must have at least 2 points.");
        }

        if (grade < 1 || grade > scaleMax)
        {
            throw new ArgumentOutOfRangeException(
                nameof(grade), grade, $"Grade must be within [1, {scaleMax}].");
        }

        Grade = grade;
        ScaleMax = scaleMax;
        Label = label;
    }

    /// <summary>The grade normalized to <c>[0, 1]</c> (worst → best) — scale-independent comparison.</summary>
    public double Normalized => (double)(Grade - 1) / (ScaleMax - 1);

    /// <inheritdoc />
    public override string ToString() => Label is null ? $"{Grade}/{ScaleMax}" : $"{Label} ({Grade}/{ScaleMax})";
}
