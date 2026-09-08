using Harborline.Api.Foundation.Forms.Models;

namespace Harborline.Api.Blocks.Assets.Registry.Model.Scoring;

/// <summary>
/// Resolves the effective per-field scoring metadata for a standard by merging its cascade layers
/// (base → pack → tenant → instance), enforcing the F1 raise-strictness-only floor rule.
/// </summary>
public interface IScoringCascadeResolver
{
    /// <summary>
    /// Merges <paramref name="layers"/> (all for the same form definition) into the effective
    /// per-field metadata, keyed by field pointer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Later layers override earlier ones (last-writer-wins per field), <b>except</b> that a
    /// seed-declared (base/pack) critical or safety <see cref="ScoringCriticality"/> floor is
    /// raise-strictness-only: a downstream layer declaring a LOWER criticality than the running
    /// seed floor is rejected with <see cref="ScoringFloorViolationException"/> (F1). Effective
    /// criticality is therefore always at least the seed floor.
    /// </para>
    /// <para>
    /// <b>Owner-side re-derivation contract (documented; compute is Wave 3):</b> the authoritative
    /// composite roll-up (item → category → discipline → composite) is always recomputed by the
    /// data owner from the signed item-answers under the resolved metadata — it is NEVER trusted as
    /// a pre-computed number synced from a delegated/vendor instance (F1). This resolver produces
    /// the metadata that a Wave-3 owner-side computation consumes; it does not itself compute or
    /// accept any composite.
    /// </para>
    /// </remarks>
    /// <exception cref="ScoringFloorViolationException">A downstream layer tried to lower a seed floor.</exception>
    /// <exception cref="ArgumentException">Layers reference different form definitions.</exception>
    IReadOnlyDictionary<string, FieldScoringMetadata> Resolve(IEnumerable<FieldScoringOverlay> layers);
}
