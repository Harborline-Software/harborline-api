using Harborline.Api.Foundation.Forms.Exceptions;
using Harborline.Api.Foundation.Forms.Models;

namespace Harborline.Api.Foundation.Forms;

/// <summary>
/// Resolves the D4 reuse cascade for a <see cref="FormDefinition"/> (ADR 0135/0140 amendments
/// 2026-07-01) — expands every <see cref="FormItemKind.Reference"/> node against the
/// reusable-unit store and merges the referenced units' properties through the ADR 0129
/// per-property lock cascade, <b>CP-locked-by-default</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Phase 1 = the all-CP-locked endpoint.</b> Every property a referenced unit provides is
/// LOCKED at the reference site: the consuming definition cannot override it. There is no
/// override channel; a consumer that declares a field the unit owns is rejected fail-closed
/// (<see cref="ReusableUnitCodes.LockedFieldOverride"/>). The per-unit AP-override endpoint is
/// deferred behind three named kill-triggers.
/// </para>
/// <para>
/// <b>Reuse-by-reference, not copy.</b> The resolver reads units live from the store, so a
/// latest-published reference reflects a newly published unit version on the next resolve —
/// the stored definition never embeds a frozen copy.
/// </para>
/// </remarks>
public interface IReuseResolver
{
    /// <summary>
    /// Resolves <paramref name="definition"/> into a reference-expanded
    /// <see cref="ResolvedFormDefinition"/> (units merged CP-locked, reference nodes replaced by
    /// groups) plus per-field reuse provenance. A definition with no reference nodes resolves to
    /// itself with an empty provenance map.
    /// </summary>
    /// <exception cref="ReuseResolutionException">A reference is unresolved, targets a
    /// workflow-subgraph unit (Phase-1 fence), attempts to override a locked field, or two
    /// references contribute the same field key. Fail-closed — nothing is partially expanded.</exception>
    ValueTask<ResolvedFormDefinition> ResolveAsync(FormDefinition definition, CancellationToken ct = default);
}
