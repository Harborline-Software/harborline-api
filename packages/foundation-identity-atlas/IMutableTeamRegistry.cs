using System.Threading;
using System.Threading.Tasks;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Foundation.IdentityAtlas;

/// <summary>
/// Write side of the team-membership store — the many-to-many <c>(ActorId ↔ TeamId)</c> edge,
/// with the role embedded on the edge. Completes ADR 0032's identity layer (the
/// <see cref="ITeamRegistry"/> read side was previously null-stubbed).
/// </summary>
/// <remarks>
/// <para>
/// Per the multi-org identity survey (#1275 §1–§4) the membership store is the single
/// write-store both views read from: <see cref="ITeamRegistry.GetMembershipsAsync"/>
/// (person → orgs) and the per-team roster (org → members). It lives in
/// <c>foundation-identity-atlas</c> and is keyed by <see cref="ActorId"/> + a raw
/// <see cref="System.Guid"/> team id (the cycle-break — foundation packages must NOT
/// reference <c>kernel-runtime.Teams.TeamId</c>).
/// </para>
/// <para>
/// One <see cref="ActorId"/> → N memberships (a person in N orgs); one team id → N members
/// (the org roster). The role lives on the edge (<see cref="TeamMembership.Role"/>) so a
/// person can be <see cref="TeamRole.Admin"/> in one org and <see cref="TeamRole.Viewer"/>
/// in another.
/// </para>
/// </remarks>
public interface IMutableTeamRegistry : ITeamRegistry
{
    /// <summary>
    /// Add (or upsert) a membership edge: <paramref name="actor"/> is a member of the team
    /// identified by the membership's <see cref="TeamMembership.TeamId"/>, with the supplied
    /// display fields + role. Idempotent — re-adding the same (actor, team) replaces the edge.
    /// </summary>
    ValueTask AddMembershipAsync(
        ActorId actor,
        TeamMembership membership,
        CancellationToken ct = default);

    /// <summary>
    /// Remove the membership edge for <paramref name="actor"/> in <paramref name="teamId"/>.
    /// Returns <c>true</c> if an edge was removed; <c>false</c> if none existed.
    /// </summary>
    ValueTask<bool> RemoveMembershipAsync(
        ActorId actor,
        System.Guid teamId,
        CancellationToken ct = default);

    /// <summary>
    /// Set the role on an existing membership edge. Returns <c>true</c> if the edge existed and
    /// was updated; <c>false</c> if the actor is not a member of that team.
    /// </summary>
    ValueTask<bool> SetRoleAsync(
        ActorId actor,
        System.Guid teamId,
        TeamRole role,
        CancellationToken ct = default);

    /// <summary>
    /// Resolve the role of <paramref name="actor"/> in <paramref name="teamId"/>, or
    /// <c>null</c> when the actor is not a member of that team.
    /// </summary>
    ValueTask<TeamRole?> GetRoleAsync(
        ActorId actor,
        System.Guid teamId,
        CancellationToken ct = default);

    /// <summary>
    /// The org-first view of the same store: all members of <paramref name="teamId"/> (the
    /// roster the trust gate / member-management surface consults). Returns an empty list when
    /// the team has no members.
    /// </summary>
    ValueTask<System.Collections.Generic.IReadOnlyList<TeamRosterEntry>> GetRosterAsync(
        System.Guid teamId,
        CancellationToken ct = default);
}

/// <summary>
/// Org-first projection of a membership edge — one member of a team, as seen from the
/// roster side (the inverse of <see cref="ITeamRegistry.GetMembershipsAsync"/>).
/// </summary>
/// <param name="Actor">The member.</param>
/// <param name="Membership">The membership record (display + role + subkey fingerprint).</param>
public sealed record TeamRosterEntry(ActorId Actor, TeamMembership Membership);
