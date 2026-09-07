using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.Governance.Policy;

namespace Harborline.Api.Foundation.Governance.Resolution;

/// <summary>
/// The composed (union) policy for a resolved field — the effects that apply, indexed
/// by the PEP trigger they fire at, plus the resolved <see cref="Aspect"/> the PEPs read
/// for roles / residency / retention (ADR 0140 D2 §3.3, §5.2). Effects are deduplicated
/// by <c>(kind, trigger)</c>; conflicts (residency intersection, regime precedence) are
/// resolved at composition time, never silently.
/// </summary>
/// <param name="Field">The field name.</param>
/// <param name="Tags">The effective tags whose bindings produced these effects.</param>
/// <param name="EffectsByTrigger">The composed effects keyed by trigger.</param>
/// <param name="Aspect">The resolved aspect (roles / residency / retention / immutability).</param>
public sealed record ResolvedFieldPolicy(
    string Field,
    IReadOnlyList<Tag> Tags,
    IReadOnlyDictionary<Trigger, IReadOnlyList<PolicyEffect>> EffectsByTrigger,
    ResolvedAspect Aspect)
{
    /// <summary>The effects firing at <paramref name="trigger"/> (empty if none).</summary>
    public IReadOnlyList<PolicyEffect> EffectsFor(Trigger trigger)
        => EffectsByTrigger.TryGetValue(trigger, out var e) ? e : Array.Empty<PolicyEffect>();

    /// <summary>True when an effect of <paramref name="kind"/> fires at <paramref name="trigger"/>.</summary>
    public bool Has(EffectKind kind, Trigger trigger)
        => EffectsFor(trigger).Any(e => e.Kind == kind);

    /// <summary>The first effect of <paramref name="kind"/> at <paramref name="trigger"/>, or null.</summary>
    public PolicyEffect? Effect(EffectKind kind, Trigger trigger)
        => EffectsFor(trigger).FirstOrDefault(e => e.Kind == kind);
}
