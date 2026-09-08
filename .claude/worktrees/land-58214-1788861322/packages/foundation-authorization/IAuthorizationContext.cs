namespace Harborline.Api.Foundation.Authorization;

/// <summary>
/// Policy-evaluation primitive. Returns <c>true</c> iff the caller has the
/// named permission. Distinct from caller identity
/// (<see cref="ICurrentUser"/>) and tenant resolution
/// (<see cref="Harborline.Api.Foundation.MultiTenancy.ITenantContext"/>) per ADR
/// 0091 Option B decomposition.
/// </summary>
/// <remarks>
/// <para>
/// <b>Introduced by ADR 0091 Step 1 (Revision 2).</b> The legacy 4-member
/// <see cref="ITenantContext"/> conflated tenant identity, caller identity,
/// and authorization on a single interface; the decomposition separates
/// concerns.
/// </para>
/// <para>
/// <b>Step 1 design debt (per O-1 ruling, Revision 2).</b>
/// <see cref="HasPermission"/> is intentionally a simple
/// <c>bool HasPermission(string)</c> surface for Step 1. Both councils
/// deferred the evolution to a claims-based shape
/// (<c>AuthorizationResult</c> / <c>IAuthorizationRequirement</c> per
/// ASP.NET Core) to the future production OIDC-impl ADR. ADR 0091 keeps
/// this surface through Step 5.
/// </para>
/// </remarks>
public interface IAuthorizationContext
{
    /// <summary>
    /// Returns <c>true</c> when the current caller is permitted to perform
    /// the named action. Implementations MUST evaluate against the same
    /// validated token instance that backs <see cref="ICurrentUser"/> — see
    /// ADR 0091 §"Out-of-scope-but-flagged" §1 (same-token invariant).
    /// </summary>
    bool HasPermission(string permission);
}
