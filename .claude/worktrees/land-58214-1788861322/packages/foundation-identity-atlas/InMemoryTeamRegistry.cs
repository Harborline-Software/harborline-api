using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Foundation.IdentityAtlas;

/// <summary>
/// In-memory <see cref="IMutableTeamRegistry"/> — the v1 membership store for ADR 0032's
/// identity layer. Keyed by <c>(ActorId, Guid TeamId)</c>; the role lives on the edge.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors the in-memory-v1 posture of <c>IPrincipalPartyResolver</c> (#216): the contract +
/// resolution are real; durable persistence (the synced append-log membership doctype per
/// survey #1275 §4) is a separate workstream that replaces this implementation behind the
/// same interface. Thread-safe via a single gate — the membership store is low-frequency
/// (admit / revoke / set-role) so a coarse lock is appropriate.
/// </para>
/// <para>
/// Both read views project from the same backing dictionary: person-first
/// (<see cref="GetMembershipsAsync"/>) and org-first (<see cref="GetRosterAsync"/>) — there
/// is one write-store, two views (survey #1275 §2a).
/// </para>
/// <para>
/// <b>This store is a PROJECTION of authority, not the authority (ADR 0066 clauses 3 and 7).</b> It is
/// process-local: everything in it is rebuilt at the next start from durable state, so
/// <see cref="RemoveMembershipAsync"/> and <see cref="SetRoleAsync"/> cannot durably remove administrative
/// authority and are deliberately NOT where the last-usable-administrator invariant lives. That invariant is
/// enforced where the removal is durable — the node host's administrator-authority log — because an invariant
/// enforced on a projection would be undone by the next boot while reading as if it had held. What a mutation
/// here CAN do is drop a running process's resolved authority until restart; migration step 4 makes membership
/// durable and closes that gap by making this store the authority's projection in fact as well as in intent.
/// </para>
/// </remarks>
public sealed class InMemoryTeamRegistry : IMutableTeamRegistry
{
    private readonly object _gate = new();

    // (actor, teamId) → membership. The composite key is the edge primary key.
    private readonly Dictionary<(ActorId Actor, Guid TeamId), TeamMembership> _edges = new();

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<TeamMembership>> GetMembershipsAsync(
        ActorId actor,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyList<TeamMembership> result = _edges
                .Where(kvp => kvp.Key.Actor.Equals(actor))
                .Select(kvp => kvp.Value)
                .ToArray();
            return ValueTask.FromResult(result);
        }
    }

    /// <inheritdoc />
    public ValueTask AddMembershipAsync(
        ActorId actor,
        TeamMembership membership,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(membership);
        lock (_gate)
        {
            _edges[(actor, membership.TeamId)] = membership;
        }
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<bool> RemoveMembershipAsync(
        ActorId actor,
        Guid teamId,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(_edges.Remove((actor, teamId)));
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> SetRoleAsync(
        ActorId actor,
        Guid teamId,
        TeamRole role,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_edges.TryGetValue((actor, teamId), out var existing))
            {
                return ValueTask.FromResult(false);
            }

            _edges[(actor, teamId)] = existing with
            {
                Role = role,
                RoleDisplayName = TeamRolePermissions.DisplayName(role),
            };
            return ValueTask.FromResult(true);
        }
    }

    /// <inheritdoc />
    public ValueTask<TeamRole?> GetRoleAsync(
        ActorId actor,
        Guid teamId,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(
                _edges.TryGetValue((actor, teamId), out var membership)
                    ? membership.Role
                    : (TeamRole?)null);
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<TeamRosterEntry>> GetRosterAsync(
        Guid teamId,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyList<TeamRosterEntry> roster = _edges
                .Where(kvp => kvp.Key.TeamId == teamId)
                .Select(kvp => new TeamRosterEntry(kvp.Key.Actor, kvp.Value))
                .ToArray();
            return ValueTask.FromResult(roster);
        }
    }
}
