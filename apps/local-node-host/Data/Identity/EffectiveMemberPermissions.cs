using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.LocalNodeHost.Data.Identity;

/// <summary>Derives roster and install-root grant inputs. AuthorizationGate alone decides an act.</summary>
internal static class EffectiveMemberPermissions
{
    private const string InstallRoot = "/";

    /// <summary>
    /// Snapshot membership, ejection and install-root permission inputs without answering an act.
    /// </summary>
    internal static async ValueTask<AuthorizationRosterInputs> ReadAsync(
        IAuthorizationClosureReader authorization,
        MemberRoster roster,
        string partyId,
        TenantId tenant,
        ActorId principal,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(roster);

        var ejected = IsEjected(roster, partyId, principal);

        // A member reconstructed from replicated records has NO roster permission set (293 s3b2) - membership
        // is Contains, never "the roster answered a set" - so the read falls through to the grant closure.
        var rosterPermissions = roster.PermissionsOf(partyId);
        if (rosterPermissions is not null || ejected)
        {
            return new(partyId, roster.Contains(partyId), ejected, rosterPermissions);
        }

        var atoms = await authorization
            .UserPermissionsAsync(tenant, principal, at, cancellationToken)
            .ConfigureAwait(false);
        var closure = PermissionSet.From(atoms.Atoms
            .Where(atom => string.Equals(atom.Scope.Value, InstallRoot, StringComparison.Ordinal))
            .Select(atom => atom.Operation.Value));

        return new(partyId, roster.Contains(partyId), ejected, closure);
    }

    internal static AuthorizationRosterInputs Read(MemberRoster roster, string partyId, ActorId principal) =>
        new(partyId, roster.Contains(partyId), IsEjected(roster, partyId, principal),
            roster.PermissionsOf(partyId));

    // During the identity migration, either existing key can carry the signed removal. Check the
    // principal the gate reads as well as the canonical party, before accepting any live edge or grant.
    private static bool IsEjected(MemberRoster roster, string partyId, ActorId principal) =>
        roster.EnumerateAdmissions().Any(admission =>
            (string.Equals(admission.PartyId, partyId, StringComparison.Ordinal)
                || string.Equals(admission.PartyId, principal.Value, StringComparison.Ordinal))
            && !roster.Contains(admission.PartyId));
}
