using Harborline.Api.Foundation.Governance.Enforcement;
using Harborline.Api.Foundation.Governance.Policy;
using Harborline.Api.Foundation.Governance.Resolution;

namespace Harborline.Api.Foundation.Governance.Bridges;

/// <summary>
/// Default <see cref="IResidencyEligibilityResolver"/> — fail-closed eligible-set computation.
/// </summary>
public sealed class DefaultResidencyEligibilityResolver : IResidencyEligibilityResolver
{
    /// <inheritdoc />
    public ResidencyEligibility Resolve(ResolvedFieldPolicy policy, Trigger trigger)
    {
        if (policy is null) throw new ArgumentNullException(nameof(policy));

        if (!policy.Has(EffectKind.Reside, trigger))
        {
            return new ResidencyEligibility(Required: false, Array.Empty<string>());
        }

        var allowed = policy.Aspect.Residency?.AllowedJurisdictions;
        var prohibited = policy.Aspect.Residency?.ProhibitedJurisdictions
            ?? (IReadOnlyList<string>)Array.Empty<string>();
        var classified = policy.Tags.Count > 0;

        if (classified && (allowed is null || allowed.Count == 0))
        {
            // The whole point of the bridge: a classified field that must reside but names
            // no allowed jurisdiction is fail-closed — never the enforcer's fail-open default.
            throw new DataResidencyViolationException(policy.Field,
                "field is classified and requires residency but declares no allowed jurisdiction (fail-closed).");
        }

        // Explicit prohibitions WIN over the positive allow-set (deep-review F3): a jurisdiction
        // present in BOTH allowed and prohibited is removed from the eligible set so it can never
        // pass the PEP's allow-check. The PEPs additionally reject target ∈ prohibited explicitly.
        IReadOnlyList<string> eligible = allowed ?? Array.Empty<string>();
        if (prohibited.Count > 0 && eligible.Count > 0)
        {
            var prohibitedSet = new HashSet<string>(prohibited, StringComparer.Ordinal);
            eligible = eligible.Where(j => !prohibitedSet.Contains(j)).ToList();
            if (classified && eligible.Count == 0)
            {
                // Every allowed jurisdiction is also prohibited ⇒ nowhere eligible ⇒ fail-closed.
                throw new DataResidencyViolationException(policy.Field,
                    "every allowed jurisdiction for the classified field is also prohibited (fail-closed).");
            }
        }

        return new ResidencyEligibility(Required: true, eligible, prohibited);
    }
}
