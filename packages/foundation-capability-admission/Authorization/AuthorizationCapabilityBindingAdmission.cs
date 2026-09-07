using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.Foundation.CapabilityAdmission.Authorization;

/// <summary>Warning codes emitted by authorization capability-binding admission.</summary>
public enum BindingWarningCode
{
    /// <summary>The tenant deliberately selected no offered roles.</summary>
    EmptyBinding = 0,
}

/// <summary>The admitted effective authorization binding and any non-fatal warning.</summary>
public sealed record AuthorizationCapabilityBindingAdmissionResult(
    RoleBindingSet EffectiveRoles,
    BindingWarningCode? Warning);

/// <summary>
/// Enforces publisher ceilings and tenant narrow-only authorization role bindings.
/// This is separate from release-manifest and runtime capability admission.
/// </summary>
public sealed class AuthorizationCapabilityBindingAdmission
{
    /// <summary>Admits a tenant selection only when it narrows both binding ceilings.</summary>
    public AuthorizationCapabilityBindingAdmissionResult Admit(
        RoleBindingSet publisherCeiling,
        RoleBindingSet currentEffectiveBinding,
        RoleBindingSet tenantSelection)
    {
        ArgumentNullException.ThrowIfNull(publisherCeiling);
        ArgumentNullException.ThrowIfNull(currentEffectiveBinding);
        ArgumentNullException.ThrowIfNull(tenantSelection);

        if (!tenantSelection.IsSubsetOf(publisherCeiling))
        {
            throw new InvalidOperationException(
                "A tenant authorization binding cannot add a role outside the publisher ceiling.");
        }

        if (!tenantSelection.IsSubsetOf(currentEffectiveBinding))
        {
            throw new InvalidOperationException(
                "A tenant authorization binding cannot re-add a role removed by an earlier narrowing.");
        }

        var effectiveRoles = publisherCeiling.Intersect(tenantSelection);
        return new AuthorizationCapabilityBindingAdmissionResult(
            effectiveRoles,
            effectiveRoles.Equals(RoleBindingSet.Empty) ? BindingWarningCode.EmptyBinding : null);
    }
}
