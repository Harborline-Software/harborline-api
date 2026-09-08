using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Forms.Models;

namespace Harborline.Api.Blocks.Assets.Registry.Model.Scoring;

/// <summary>
/// Default <see cref="IScoringCascadeResolver"/> — merges scoring overlays with the F1
/// raise-strictness-only seed-floor rule.
/// </summary>
public sealed class ScoringCascadeResolver : IScoringCascadeResolver
{
    /// <inheritdoc />
    public IReadOnlyDictionary<string, FieldScoringMetadata> Resolve(IEnumerable<FieldScoringOverlay> layers)
    {
        ArgumentNullException.ThrowIfNull(layers);

        // Deterministic order: ascending cascade layer (base → pack → tenant → instance).
        var ordered = layers.OrderBy(l => (int)l.Layer).ToList();
        if (ordered.Count == 0)
        {
            return new Dictionary<string, FieldScoringMetadata>(StringComparer.Ordinal);
        }

        var formDefinition = ordered[0].FormDefinition;
        foreach (var layer in ordered)
        {
            if (!layer.FormDefinition.Equals(formDefinition))
            {
                throw new ArgumentException(
                    $"All scoring overlays must be for the same form definition. Expected "
                    + $"'{formDefinition}' but saw '{layer.FormDefinition}'.",
                    nameof(layers));
            }
        }

        var fieldPointers = ordered
            .SelectMany(l => l.Fields.Keys)
            .Distinct(StringComparer.Ordinal);

        var result = new Dictionary<string, FieldScoringMetadata>(StringComparer.Ordinal);
        foreach (var fieldPointer in fieldPointers)
        {
            result[fieldPointer] = ResolveField(fieldPointer, ordered);
        }

        return result;
    }

    private static FieldScoringMetadata ResolveField(string fieldPointer, IReadOnlyList<FieldScoringOverlay> ordered)
    {
        FieldScoringMetadata? merged = null;

        // The strictest criticality any SEED (base/pack) layer has declared so far, and where.
        var seedFloor = ScoringCriticality.Normal;
        var seedFloorLayer = CascadeLayer.Base;
        var seedFloorSet = false;

        foreach (var layer in ordered)
        {
            if (!layer.Fields.TryGetValue(fieldPointer, out var entry))
            {
                continue;
            }

            if (layer.Layer.IsSeed())
            {
                // A later seed may raise the floor but never lower it (Pack is downstream of Base).
                if (seedFloorSet && entry.Criticality < seedFloor)
                {
                    throw new ScoringFloorViolationException(
                        fieldPointer, seedFloor, seedFloorLayer, entry.Criticality, layer.Layer);
                }

                if (!seedFloorSet || entry.Criticality > seedFloor)
                {
                    seedFloor = entry.Criticality;
                    seedFloorLayer = layer.Layer;
                    seedFloorSet = true;
                }
            }
            else
            {
                // A downstream (tenant/instance) layer may raise strictness but never lower a
                // seed-declared floor.
                if (seedFloorSet && entry.Criticality < seedFloor)
                {
                    throw new ScoringFloorViolationException(
                        fieldPointer, seedFloor, seedFloorLayer, entry.Criticality, layer.Layer);
                }
            }

            // Last-writer-wins for the whole entry; criticality is then raised to the seed floor
            // (a no-op here because any lower value was already rejected above).
            merged = entry.Criticality < seedFloor
                ? entry with { Criticality = seedFloor }
                : entry;
        }

        // fieldPointer came from the union of layer keys, so at least one layer declared it.
        return merged!;
    }
}
