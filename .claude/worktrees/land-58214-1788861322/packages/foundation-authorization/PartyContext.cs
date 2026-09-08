using System;
using System.Threading;
using System.Threading.Tasks;

namespace Harborline.Api.Foundation.Authorization;

/// <summary>
/// Default <see cref="IPartyContext"/> implementation. Reads the current principal's UserId
/// and TenantId off the SINGLE injected <see cref="ITenantContext"/> sum-interface and
/// delegates the (tenant, user) → party lookup to <see cref="IPrincipalPartyResolver"/>.
/// </summary>
/// <remarks>
/// <b>Same-token derivation is structural.</b> Both the UserId (<see cref="ICurrentUser.UserId"/>)
/// and the TenantId (<see cref="Harborline.Api.Foundation.MultiTenancy.ITenantContext.Tenant"/>) come
/// from the one <see cref="ITenantContext"/> instance — wired by <c>AddHarborlineTenantContext</c>
/// to be the same scoped object that backs all four authorization interfaces. There is no seam
/// through which a foreign UserId, a foreign tenant, or a body-supplied party id could enter.
/// </remarks>
public sealed class PartyContext : IPartyContext
{
    private readonly ITenantContext _principal;
    private readonly IPrincipalPartyResolver _resolver;

    public PartyContext(ITenantContext principal, IPrincipalPartyResolver resolver)
    {
        _principal = principal ?? throw new ArgumentNullException(nameof(principal));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    /// <inheritdoc />
    public async ValueTask<Guid> GetCurrentPartyIdAsync(CancellationToken ct = default)
    {
        var userId = _principal.UserId;
        var tenant = _principal.Tenant;

        if (string.IsNullOrWhiteSpace(userId) || tenant is null)
        {
            throw PrincipalPartyResolutionException.NoAuthenticatedPrincipal();
        }

        var partyId = await _resolver.ResolveAsync(userId, tenant.Id, ct).ConfigureAwait(false);

        return partyId
            ?? throw PrincipalPartyResolutionException.NoPartyForPrincipal(userId, tenant.Id);
    }
}
