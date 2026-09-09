using Microsoft.Extensions.DependencyInjection;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.LocalNodeHost.Data.Identity;

/// <summary>Derives roster membership and ejection inputs. AuthorizationGate alone decides an act.</summary>
/// <remarks>
/// <b>Ticket 294 slice 2a — a grant whose subject holds no roster edge is INERT AT READ TIME, never
/// refused at write time.</b> That is already what this reading produces: <c>RequireMember</c> constrains
/// the gate, an ejected subject reads the empty set, and a subject with no edge falls through to its
/// grant closure and is then denied by whatever the act requires. Refusing such a grant when it is
/// WRITTEN would re-hide it from the holders list and from revocation, which is exactly the defect
/// ticket 294 slice 1 fixed by keeping an unattributed grant listed and revocable. So: write it, list
/// it, revoke it — and let the roster decide what it reaches.
/// </remarks>
internal static class EffectiveMemberPermissions
{
    internal static AuthorizationRosterInputs Read(MemberRoster roster, string partyId, ActorId principal) =>
        new(partyId, roster.Contains(partyId), IsEjected(roster, partyId, principal));

    // During the identity migration, either existing key can carry the signed removal. Check the
    // principal the gate reads as well as the canonical party, before accepting any live edge or grant.
    private static bool IsEjected(MemberRoster roster, string partyId, ActorId principal) =>
        roster.EnumerateAdmissions().Any(admission =>
            (string.Equals(admission.PartyId, partyId, StringComparison.Ordinal)
                || string.Equals(admission.PartyId, principal.Value, StringComparison.Ordinal))
            && !roster.Contains(admission.PartyId));
}

/// <summary>
/// The production <see cref="IRosterAuthority"/> (ticket 293 slice 3c). No permission set rides the wire, so the
/// replicated path's admitter, revoker, no-escalation and never-brick gates read a party's authority from the
/// local grant store through <see cref="AuthorizationGate"/>. No verdict is computed here: every act is still
/// decided by <c>AuthorizationGate.DecideAsync</c>.
/// </summary>
/// <remarks>
/// A composition with no authorization gate (a minimal DI test) answers the empty set, which is the fail-closed floor
/// the interface documents - only the genesis chain root holds authority. The synchronous
/// <see cref="IRosterAuthority.PermissionsFor"/> contract, called from inside the synchronous rebuild, forces the
/// same single bridge <c>ActiveTeamAuthorizationContext</c> already uses; the rebuild reads each party once.
/// </remarks>
internal sealed class GrantStoreRosterAuthority(AuthorizationGate? gate) : IRosterAuthority
{
    /// <summary>
    /// The composition seam: the authorization gate is resolved HERE. Absent (a minimal DI test) - the
    /// fail-closed floor.
    /// </summary>
    internal static IRosterAuthority FromServices(IServiceProvider services) =>
        new GrantStoreRosterAuthority(services.GetService<AuthorizationGate>());

    // NO clock. The instant is the caller's - the rebuild's / the decision's At (ticket 216: only the host
    // composition root introduces wall time, and a grant read at a LATER instant than the act it feeds can flip
    // the answer mid-act).
    public PermissionSet PermissionsFor(string teamId, string partyId, DateTimeOffset at)
    {
        // One tenant-key form: the canonical "D" Guid. A team id that is not one is not a roster-backed team.
        if (gate is null
            || string.IsNullOrWhiteSpace(partyId)
            || !Guid.TryParse(teamId, out var team))
        {
            return PermissionSet.Empty;
        }

        var pending = gate.InstallRootPermissionsAsync(
            new ActorId(partyId), new TenantId(team.ToString("D")), at, CancellationToken.None);
        return pending.IsCompletedSuccessfully ? pending.Result : pending.AsTask().GetAwaiter().GetResult();
    }
}
