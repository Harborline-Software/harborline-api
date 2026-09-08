using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Forms.Models;

namespace Harborline.Api.Blocks.Assets.Registry.Model.Scoring;

/// <summary>
/// An <b>immutable living-standard catalog seed</b> shipped by the platform base or a pack (annex §3.8
/// D-P; ADR 0101 Rev 3.1 Wave 3a). A "living standard" is <i>a form (the item catalog) + a per-field
/// scoring aspect</i> — this seed bundles the scoring aspect (<see cref="ScoringOverlay"/>) plus the
/// condition-rating <see cref="Bindings"/> that make a submission of that form project typed condition
/// records. The item-catalog FORM itself is a <see cref="FormDefinition"/> a host registers/publishes; a
/// seed only pins the scoring + binding config that rides it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Immutable, shared, seed-layer (F2 invariant 1 — mirrors <see cref="EntityTypeSeed"/>).</b> A seed
/// is NOT tenant-scoped; it is shared read-only catalog config. A tenant that "tunes" a standard NEVER
/// mutates the seed — the store produces a separate tenant-scoped scoring overlay row, and the effective
/// metadata is the <see cref="IScoringCascadeResolver"/> merge of [seed overlay, tenant overlay] under the
/// raise-strictness-only floor rule (F1). <see cref="Provenance"/> is always a seed layer
/// (<see cref="CascadeLayer.Base"/>/<see cref="CascadeLayer.Pack"/>) and is DERIVED from the overlay's
/// layer, so a catalog can never disagree with its own scoring layer.
/// </para>
/// <para>
/// <b>NO compute (Rev 3.1 fold).</b> A seed carries only the per-field scoring METADATA; the composite
/// roll-up (item → category → discipline → composite) is Wave-3b owner-side compute, never precomputed
/// or stored on the seed.
/// </para>
/// </remarks>
/// <param name="Key">Stable catalog key (shared across tenants), e.g. <c>living-standard.residential.slice.v1</c>.</param>
/// <param name="Title">A human-readable catalog label (the localized FORM title lives on the form definition).</param>
/// <param name="ScoringOverlay">The seed-layer per-field scoring metadata for the catalog's item form.</param>
/// <param name="Bindings">The condition-rating field bindings whose submission projects a typed condition record.</param>
public sealed record StandardCatalogSeed(
    string Key,
    string Title,
    FieldScoringOverlay ScoringOverlay,
    IReadOnlyList<ConditionRatingFieldBinding> Bindings)
{
    /// <summary>The item-catalog form this standard scores (derived from the overlay — never inconsistent).</summary>
    public FormDefinitionId FormDefinition => ScoringOverlay.FormDefinition;

    /// <summary>The seed layer this catalog was declared at (derived from the overlay).</summary>
    public CascadeLayer Provenance => ScoringOverlay.Layer;

    /// <summary>Validates the invariants a seed must always hold (called by the store on seed).</summary>
    /// <exception cref="ArgumentException">Blank key, a non-seed layer, or a binding targeting another form.</exception>
    public StandardCatalogSeed Validated()
    {
        if (string.IsNullOrWhiteSpace(Key))
        {
            throw new ArgumentException("A standard catalog must have a non-empty key.", nameof(Key));
        }

        if (!ScoringOverlay.Layer.IsSeed())
        {
            throw new ArgumentException(
                $"A catalog seed must be declared at a seed layer (Base/Pack); got {ScoringOverlay.Layer}.",
                nameof(ScoringOverlay));
        }

        ArgumentNullException.ThrowIfNull(Bindings);
        foreach (var binding in Bindings)
        {
            if (!binding.FormDefinition.Equals(FormDefinition))
            {
                throw new ArgumentException(
                    $"Binding on field '{binding.FieldPointer}' targets form '{binding.FormDefinition}' but the "
                    + $"catalog's item form is '{FormDefinition}'. A catalog's bindings must rate its own form.",
                    nameof(Bindings));
            }
        }

        return this;
    }
}
