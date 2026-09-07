using System.Collections.Generic;

namespace Harborline.Api.Foundation.Authorization;

/// <summary>
/// Caller identity. Single-responsibility surface for the OIDC seam — carries
/// the authenticated user's stable id + role list. Distinct from tenant
/// resolution (<see cref="Harborline.Api.Foundation.MultiTenancy.ITenantContext"/>)
/// and policy evaluation (<see cref="IAuthorizationContext"/>) per ADR 0091
/// Option B decomposition.
/// </summary>
/// <remarks>
/// <para>
/// <b>Introduced by ADR 0091 Step 1 (Revision 2).</b> The legacy 4-member
/// <see cref="ITenantContext"/> conflated tenant identity, caller identity,
/// and authorization on a single interface; the decomposition separates
/// concerns so each consumer can declare exactly what it needs.
/// </para>
/// <para>
/// <b>Future production OIDC-impl ADR.</b> When the demo seam
/// <c>DemoTenantContext</c> is replaced with a production OIDC-claims-backed
/// impl (tracked in ADR 0091 §"Out-of-scope-but-flagged" / future ADR), the
/// impl MUST source <see cref="UserId"/> + <see cref="Roles"/> + the input to
/// <see cref="IAuthorizationContext.HasPermission"/> from the SAME validated
/// token instance. Reading <c>sub</c> from one source and <c>roles</c> from
/// another is the textbook confused-deputy seam.
/// </para>
/// </remarks>
public interface ICurrentUser
{
    /// <summary>Stable user identifier (e.g., OIDC <c>sub</c> claim, internal user-record id).</summary>
    string UserId { get; }

    /// <summary>Role names asserted for the caller. May be empty.</summary>
    IReadOnlyList<string> Roles { get; }
}
