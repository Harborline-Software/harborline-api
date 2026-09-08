using System;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Foundation.Authorization;

/// <summary>
/// Thrown by <see cref="IPartyContext.GetCurrentPartyIdAsync"/> when the current principal
/// cannot be resolved to a PartyId — either no authenticated principal is present on the
/// scope, or no party maps to the principal's (tenant, user) pair. Both are server-side
/// mis-provisioning conditions, NOT normal request flow; hosts map this to a 403/500 rather
/// than treating it as a routine outcome.
/// </summary>
public sealed class PrincipalPartyResolutionException : Exception
{
    private PrincipalPartyResolutionException(string message) : base(message) { }

    /// <summary>No authenticated principal was present on the current scope.</summary>
    public static PrincipalPartyResolutionException NoAuthenticatedPrincipal() =>
        new("Cannot resolve a PartyId: no authenticated principal is present on the current "
            + "scope (UserId or Tenant is unset). IPartyContext requires an authenticated, "
            + "tenant-resolved principal.");

    /// <summary>An authenticated principal was present but no party maps to it.</summary>
    public static PrincipalPartyResolutionException NoPartyForPrincipal(string userId, TenantId tenantId) =>
        new($"No party maps to the authenticated principal (userId='{userId}', "
            + $"tenant='{tenantId}'). The principal is authenticated but not provisioned with "
            + "a party in this tenant.");
}
