using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Forms.Models;

namespace Harborline.Api.Blocks.Assets.Registry.Model.Scoring;

/// <summary>
/// One cascade layer's worth of per-field scoring metadata for a single form definition (a
/// standard's item catalog) — the config layer A2 keeps <b>separate</b> from the governance
/// overlay.
/// </summary>
/// <remarks>
/// <para>
/// A standard is a form definition + this per-field scoring config. Each layer of the cascade
/// (base → pack → tenant → instance) contributes its own <see cref="FieldScoringOverlay"/>; the
/// <see cref="ScoringCascadeResolver"/> merges them into the effective metadata, enforcing the F1
/// raise-strictness-only floor rule. Every entry's <see cref="FieldScoringMetadata.Provenance"/>
/// must equal this overlay's <see cref="Layer"/> (the constructor validates it).
/// </para>
/// </remarks>
public sealed class FieldScoringOverlay
{
    private readonly IReadOnlyDictionary<string, FieldScoringMetadata> _byField;

    /// <summary>The form definition (standard item catalog) this overlay scores.</summary>
    public FormDefinitionId FormDefinition { get; }

    /// <summary>The cascade layer this overlay sits at.</summary>
    public CascadeLayer Layer { get; }

    /// <summary>The per-field metadata entries, keyed by field pointer.</summary>
    public IReadOnlyDictionary<string, FieldScoringMetadata> Fields => _byField;

    /// <summary>
    /// Constructs a layer overlay, validating each entry and that its declared provenance matches
    /// <paramref name="layer"/>.
    /// </summary>
    public FieldScoringOverlay(
        FormDefinitionId formDefinition,
        CascadeLayer layer,
        IEnumerable<FieldScoringMetadata> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        FormDefinition = formDefinition;
        Layer = layer;

        var map = new Dictionary<string, FieldScoringMetadata>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            field.Validated();
            if (field.Provenance != layer)
            {
                throw new ArgumentException(
                    $"Scoring metadata for field '{field.FieldPointer}' declares provenance "
                    + $"{field.Provenance} but sits in a {layer} overlay. Provenance must match the "
                    + "layer so a floor's origin is never misattributed (F1).",
                    nameof(fields));
            }

            map[field.FieldPointer] = field;
        }

        _byField = map;
    }
}
