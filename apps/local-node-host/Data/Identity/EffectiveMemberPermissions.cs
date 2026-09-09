using Harborline.Api.Blocks.AccessGrant;
using Microsoft.Extensions.DependencyInjection;
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

        var closure = await InstallRootPermissionsAsync(
            authorization, tenant, principal, at, cancellationToken).ConfigureAwait(false);

        return new(partyId, roster.Contains(partyId), ejected, closure);
    }

    /// <summary>
    /// The install-root grant derivation both readings answer from: the principal's closure atoms at the
    /// install root, as a permission set. The gate decides an act; this only derives its grant input.
    /// </summary>
    internal static async ValueTask<PermissionSet> InstallRootPermissionsAsync(
        IAuthorizationClosureReader authorization,
        TenantId tenant,
        ActorId principal,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        var atoms = await authorization
            .UserPermissionsAsync(tenant, principal, at, cancellationToken)
            .ConfigureAwait(false);
        return PermissionSet.From(atoms.Atoms
            .Where(atom => string.Equals(atom.Scope.Value, InstallRoot, StringComparison.Ordinal))
            .Select(atom => atom.Operation.Value));
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

/// <summary>
/// The production <see cref="IRosterAuthority"/> (ticket 293 slice 3c). No permission set rides the wire, so the
/// replicated path's admitter, revoker, no-escalation and never-brick gates read a party's authority from the
/// local grant store - through the SAME install-root derivation
/// <see cref="EffectiveMemberPermissions.ReadAsync"/> uses, in the one file the gate fence sanctions as the
/// roster-edge-then-closure reading. No verdict is computed here: a permission SET is the gate's roster input,
/// and every act is still decided by <c>AuthorizationGate.DecideAsync</c>.
/// </summary>
/// <remarks>
/// A composition with no grant store (a minimal DI test) answers the empty set, which is the fail-closed floor
/// the interface documents - only the genesis chain root holds authority. The synchronous
/// <see cref="IRosterAuthority.PermissionsFor"/> contract, called from inside the synchronous rebuild, forces the
/// same single bridge <c>ActiveTeamAuthorizationContext</c> already uses; the rebuild reads each party once.
/// </remarks>
internal sealed class GrantStoreRosterAuthority(
    IAuthorizationClosureReader? authorization,
    TimeProvider clock) : IRosterAuthority
{
    /// <summary>
    /// The composition seam: the grant store is resolved HERE, in the fence's sanctioned reading, so no
    /// composition file names the closure reader. Absent (a minimal DI test) - the fail-closed floor.
    /// </summary>
    internal static IRosterAuthority FromServices(IServiceProvider services) =>
        new GrantStoreRosterAuthority(
            services.GetService<IAuthorizationClosureReader>(),
            services.GetService<TimeProvider>() ?? TimeProvider.System);

    public PermissionSet PermissionsFor(string teamId, string partyId)
    {
        // One tenant-key form: the canonical "D" Guid. A team id that is not one is not a roster-backed team.
        if (authorization is null
            || string.IsNullOrWhiteSpace(partyId)
            || !Guid.TryParse(teamId, out var team))
        {
            return PermissionSet.Empty;
        }

        var pending = EffectiveMemberPermissions.InstallRootPermissionsAsync(
            authorization,
            new TenantId(team.ToString("D")),
            new ActorId(partyId),
            clock.GetUtcNow(),
            CancellationToken.None);
        return pending.IsCompletedSuccessfully ? pending.Result : pending.AsTask().GetAwaiter().GetResult();
    }
}
