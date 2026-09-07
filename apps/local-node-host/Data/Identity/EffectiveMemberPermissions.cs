using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.LocalNodeHost.Data.Identity;

/// <summary>
/// The ONE reading of "what may this party do here" behind every members:manage-gated surface: the signed
/// roster edge's permission set, or - for a party the roster does not carry - the install-wide atoms ticket
/// 205's evaluator derives from that party's live grants.
/// </summary>
/// <remarks>
/// <para>
/// <b>The signed roster edge wins where it exists.</b> Narrowing a member's edge takes the permission away
/// on the next request even while their grant is untouched (<c>SelectedSessionPepTests</c>), so the closure
/// is not unioned in over the top of it. The closure is the authority for a party the roster does not carry
/// — the grant-anchored web member of the Option A ruling, and the Administrator successor of ticket 211
/// slice 2's handover (L618), whose new grant is the only record of the role they were just handed. Reading
/// the roster edge alone left that successor unable to manage members at all, which is what this replaces.
/// </para>
/// <para>
/// <b>Ejection still wins.</b> A party the signed roster has an admission record for but no permission edge
/// on has been ejected by the roster plane; a grant that was not revoked alongside does not restore them.
/// That refusal is answered before the closure is consulted.
/// </para>
/// <para>
/// Only atoms held over the install root count. A record-scoped grant administers that record, not the
/// installation — the same reading <see cref="LastAdministratorGuard.IsAdministratorInForce"/> takes.
/// </para>
/// </remarks>
internal static class EffectiveMemberPermissions
{
    private const string InstallRoot = "/";

    /// <summary>
    /// The effective install-wide permission set for <paramref name="partyId"/>, or null when the party
    /// holds nothing here (including the ejected case, which is a refusal rather than an empty set).
    /// </summary>
    internal static async ValueTask<PermissionSet?> ResolveAsync(
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

        var rosterPermissions = roster.PermissionsOf(partyId);
        if (rosterPermissions is not null)
        {
            return rosterPermissions;
        }

        if (roster.EnumerateAdmissions().Any(admission =>
                string.Equals(admission.PartyId, partyId, StringComparison.Ordinal)))
        {
            return null;
        }

        var atoms = await authorization
            .UserPermissionsAsync(tenant, principal, at, cancellationToken)
            .ConfigureAwait(false);
        var closure = PermissionSet.From(atoms.Atoms
            .Where(atom => string.Equals(atom.Scope.Value, InstallRoot, StringComparison.Ordinal))
            .Select(atom => atom.Operation.Value));

        return closure.Count == 0 ? null : closure;
    }

    /// <summary>
    /// Whether minting <paramref name="partyId"/> an install-wide Administrator grant would actually let
    /// them manage members — the question ticket 211's handover has to answer before it moves the role.
    /// The signed roster edge is authoritative where it exists, so a roster member whose edge lacks
    /// members:manage, and a party the roster has ejected, cannot be made an administrator by grant alone.
    /// A roster-ABSENT party reads their permissions from the closure, which the minted grant supplies.
    /// </summary>
    internal static bool AnAdministratorGrantWouldConferMembersManage(MemberRoster roster, string partyId)
    {
        ArgumentNullException.ThrowIfNull(roster);

        var rosterPermissions = roster.PermissionsOf(partyId);
        return rosterPermissions is not null
            ? rosterPermissions.Contains(TeamRolePermissions.MembersManage)
            : !roster.EnumerateAdmissions().Any(admission =>
                string.Equals(admission.PartyId, partyId, StringComparison.Ordinal));
    }
}
