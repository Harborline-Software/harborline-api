namespace Harborline.Api.Blocks.Assets.Registry.Model.Scoring;

/// <summary>
/// The kind of a scored item (annex §3.8, D-R). Three kinds, matching the living-standard item
/// catalog vocabulary.
/// </summary>
public enum ScoringItemKind
{
    /// <summary>The common ordinal rating (e.g. 1–5).</summary>
    Rating = 0,

    /// <summary>
    /// A numeric reading with units and an acceptable range (air quality, water temperature),
    /// mapped to score bands and also stored as a reading history on the entity. See
    /// <see cref="FieldScoringMetadata.MeasurementRange"/>.
    /// </summary>
    Measurement = 1,

    /// <summary>
    /// A binary pass/fail check (working smoke detectors, working locks). When
    /// <see cref="ScoringCriticality.SafetyCritical"/>, a fail is never averaged away.
    /// </summary>
    SafetyPassFail = 2,
}
