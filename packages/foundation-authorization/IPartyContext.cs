using System;
using System.Threading;
using System.Threading.Tasks;

namespace Harborline.Api.Foundation.Authorization;

/// <summary>
/// Ambient accessor for the CURRENT authenticated principal's <see cref="Guid"/> PartyId.
/// This is the canonical principal→party resolution seam the platform names as its
/// Halt-1 follow-up (see <c>blocks-people-foundation</c>'s <c>IPartyWriteService</c>):
/// the one place a write path translates "who is calling" into the server-derived party
/// identity used for ownership, authorship, and approval checks.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ambient by design — no id is threaded through call sites.</b> The accessor takes no
/// principal/user/party parameters; it reads the UserId and TenantId off the single
/// injected <see cref="ITenantContext"/> sum-interface (which is both
/// <see cref="ICurrentUser"/> and <see cref="Harborline.Api.Foundation.MultiTenancy.ITenantContext"/>),
/// so the resolved party is always derived from the SAME validated token that carries the
/// caller's identity. This is the confused-deputy guard the <see cref="ICurrentUser"/>
/// doc warns about, realized in code: a consumer cannot combine a UserId from one
/// principal with a tenant from another, and cannot supply a body-controlled party id.
/// </para>
/// <para>
/// <b>Server-derived, never body-supplied.</b> Because there are no parameters, mutating
/// endpoints have no seam through which a request body could inject an
/// <c>actingPartyId</c>/<c>workerPartyId</c>/<c>approverPartyId</c>/<c>rejecterPartyId</c>/
/// <c>createdBy</c>/<c>updatedBy</c>/<c>partyId</c>. Callers performing a self-action guard
/// (e.g. self-approval) resolve the party ONCE via this accessor and compare that single
/// value on both sides of the equality check.
/// </para>
/// </remarks>
public interface IPartyContext
{
    /// <summary>
    /// Resolves the current authenticated principal's PartyId. Throws
    /// <see cref="PrincipalPartyResolutionException"/> when no principal is present or no
    /// party maps to the principal (a mis-provisioned principal — never a normal control
    /// path), so the return value is always a usable, server-derived <see cref="Guid"/>.
    /// </summary>
    ValueTask<Guid> GetCurrentPartyIdAsync(CancellationToken ct = default);
}
