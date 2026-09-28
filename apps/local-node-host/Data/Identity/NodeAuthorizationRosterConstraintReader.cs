using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Roster;

namespace Harborline.Api.LocalNodeHost.Data.Identity;

/// <summary>Derives the authorization gate's roster facts from durable tenant and People authority.</summary>
public sealed class NodeAuthorizationRosterConstraintReader(
    ICanonicalPrincipalPartyReader parties,
    IVerifiedTenantRosterReader rosters,
    ITeamRegistry? memberships = null,
    IOperationSigner? nodeSigner = null) : IAuthorizationRosterConstraintReader
{
    /// <inheritdoc />
    public async ValueTask<AuthorizationRosterInputs?> ReadAsync(
        ActorId principal,
        TenantId tenant,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var party = await parties.ResolveAsync(
                tenant, new PrincipalUserId(principal.Value), cancellationToken).ConfigureAwait(false);
            var roster = await rosters.ReadAsync(tenant, cancellationToken).ConfigureAwait(false);
            var registryMember = false;
            if (memberships is not null && Guid.TryParse(tenant.Value, out var teamId))
            {
                registryMember = (await memberships.GetMembershipsAsync(principal, cancellationToken)
                        .ConfigureAwait(false))
                    .Any(membership => membership.TeamId == teamId);
            }
            // A People binding is attribution, not the existence of the signed roster fact. Grants may
            // intentionally name an unattributed principal, and the roster can still prove that principal's
            // live edge (or its absence) directly. Only an unreadable roster makes the derivation absent.
            // Ticket 294 slice 3: the desktop actor is this node's operator, so its roster edge is the one bound
            // to this node's signing key; without this the ejection refusal never reached the desktop plane.
            var partyId = (principal.Value == ActiveTeamAuthorizationContext.LocalUserId && nodeSigner is not null
                    ? EffectiveMemberPermissions.NodeParty(roster, nodeSigner)
                    : null)
                ?? party?.PartyId.Value ?? principal.Value;
            return EffectiveMemberPermissions.Read(roster, partyId, principal) with
            {
                RegistryMember = registryMember,
            };
        }
        catch (VerifiedTenantRosterRefusedException)
        {
            return null;
        }
    }
}
