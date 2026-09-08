using Harborline.Api.Foundation.Governance.Policy;
using Harborline.Api.Foundation.Governance.Resolution;

namespace Harborline.Api.Foundation.Governance.Bridges;

/// <summary>
/// The greenfield eligible-set residency bridge (ADR 0140 D2 §7 / the ADR 0139 §D2 upgrade).
/// The shipped <c>IDataResidencyEnforcer</c> is boolean and fails OPEN when unconstrained;
/// this resolver computes the eligible jurisdiction set for a field that must reside and
/// fails CLOSED for a classified field that declares no allowed set — it never inherits the
/// enforcer's fail-open default.
/// </summary>
public interface IResidencyEligibilityResolver
{
    /// <summary>
    /// Compute residency eligibility for <paramref name="policy"/> at <paramref name="trigger"/>.
    /// </summary>
    /// <exception cref="Enforcement.DataResidencyViolationException">
    /// A classified field has a <c>Reside</c> effect at the trigger but declares no allowed
    /// jurisdiction (fail-closed).</exception>
    ResidencyEligibility Resolve(ResolvedFieldPolicy policy, Trigger trigger);
}

/// <summary>
/// The residency decision for a field at a trigger.
/// </summary>
/// <param name="Required">True when a <c>Reside</c> effect fires at this trigger.</param>
/// <param name="AllowedJurisdictions">The eligible set (intersected across grains, with any
/// <see cref="ProhibitedJurisdictions"/> already subtracted); empty only when
/// <see cref="Required"/> is false (or every allowed jurisdiction was prohibited — which is a
/// fail-closed reject before this is returned for a classified field).</param>
/// <param name="ProhibitedJurisdictions">Explicit prohibitions (ADR 0139 residency). A target
/// in this set is rejected by the Store/Export PEP even if it also appears in the allow-set —
/// prohibition WINS. Surfaced so the PEP can emit a specific "prohibited" error.</param>
public sealed record ResidencyEligibility(
    bool Required,
    IReadOnlyList<string> AllowedJurisdictions,
    IReadOnlyList<string> ProhibitedJurisdictions)
{
    /// <summary>Construct with no explicit prohibitions.</summary>
    public ResidencyEligibility(bool Required, IReadOnlyList<string> AllowedJurisdictions)
        : this(Required, AllowedJurisdictions, Array.Empty<string>()) { }
}
