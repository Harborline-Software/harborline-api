namespace Harborline.Api.Blocks.Assets.Registry.Model.Scoring;

/// <summary>
/// The acceptable range for a <see cref="ScoringItemKind.Measurement"/> item (annex §3.8, D-R) —
/// a numeric reading with units mapped to score bands (e.g. water temperature 49–60 °C).
/// </summary>
/// <remarks>Compute (mapping a reading to a band) is Wave 3; this is the declarative range only.</remarks>
/// <param name="Units">The unit of measure (e.g. "°C", "ppm"). Opaque string.</param>
/// <param name="AcceptableMin">Inclusive lower bound of the acceptable range, if any.</param>
/// <param name="AcceptableMax">Inclusive upper bound of the acceptable range, if any.</param>
public sealed record MeasurementRange(
    string Units,
    decimal? AcceptableMin = null,
    decimal? AcceptableMax = null);
