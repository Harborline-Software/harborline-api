using Harborline.Api.Foundation.CapabilityAdmission;

namespace Harborline.Api.LocalNodeHost.Tests.Identity.Mtw00CRedFixtures;

/// <summary>
/// Red fixtures for the capability side-door fence (feeds MTW-01F and Phase 8B CAP-0*): the
/// `identity.multi-tenant-web/v1` capability is release-controlled and NOT tenant-toggleable. Its
/// `EffectiveCapabilityState` derives only from a verified signed manifest, exact registrations,
/// readiness probes, and evidence. No operator flag, route registration, DI presence, pack install,
/// per-tenant activation, or global-disable control may turn an absent / unadmitted / unready /
/// manifest-mismatched capability ON — the operator control is disable-only. The production runtime
/// catalog / effective-state / no-side-door fence is unbuilt, so each invariant is red.
///
/// These fixtures model the side doors as inert in-test facts only; they never call a production
/// enablement path (the meta-test's no-production-backdoor scan asserts this structurally).
/// </summary>
internal static class CapabilitySideDoorRedFixtures
{
    private const string Domain = "capability-side-door";

    internal static IEnumerable<RedFixture> Fixtures()
    {
        yield return Production.Fixture(
            Domain,
            "operator_flag_cannot_enable_unadmitted_capability",
            authority: "capability-no-operator-enable",
            reason:
                "no operator flag may turn an absent, unadmitted, unready, or manifest-mismatched " +
                "capability on; the operator control is disable-only.",
            restartShaped: false,
            authorityType: typeof(RuntimeCapabilityDisableFence),
            proof: CapabilitySideDoorAuthorityProof.ProveOperatorControlIsDisableOnly);

        yield return Production.Fixture(
            Domain,
            "route_registration_or_di_presence_cannot_enable_capability",
            authority: "capability-no-registration-sidedoor",
            reason:
                "no route registration, DI presence, or environment flag may make EffectiveCapabilityState " +
                "Ready; readiness derives only from verified manifest, registrations, probes, and evidence.",
            restartShaped: false,
            authorityType: typeof(RuntimeCapabilityCatalog),
            proof: CapabilitySideDoorAuthorityProof.ProveRegistrationCannotManufactureAdmission);

        yield return Production.Fixture(
            Domain,
            "pack_install_or_per_tenant_activation_cannot_enable_capability",
            authority: "capability-no-pack-or-tenant-activation",
            reason:
                "the capability is release-controlled, not tenant-toggleable; no pack install or per-tenant " +
                "activation consumer may enable it, and no tenant-activation lookup exists.",
            restartShaped: false,
            authorityType: typeof(RuntimeCapabilityCatalog),
            proof: CapabilitySideDoorAuthorityProof.ProveCatalogHasNoPackOrTenantActivationInput);

        yield return Production.Fixture(
            Domain,
            "global_disable_is_veto_only_never_enable",
            authority: "capability-disable-is-veto-only",
            reason:
                "the global disable/kill switch may force an admitted capability unavailable but can never " +
                "turn an absent, unadmitted, unready, or manifest-mismatched capability on.",
            restartShaped: false,
            authorityType: typeof(RuntimeCapabilityDisableFence),
            proof: CapabilitySideDoorAuthorityProof.ProveGlobalDisableIsVetoOnly);
    }
}
