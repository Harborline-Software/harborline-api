using System;
using System.Collections.Generic;
using System.Linq;
using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Enrollment;

namespace Harborline.Api.LocalNodeHost.Data.Financial;

/// <summary>
/// Node-resident per-org role resolution (ADR 0032 identity layer; survey #1275 §3) — implements
/// <see cref="ICurrentUser"/> + <see cref="IAuthorizationContext"/> by resolving the OS-user's role
/// on the membership edge for the <em>active</em> org and projecting it to permission strings.
/// </summary>
/// <remarks>
/// <para>
/// <b>The resolution chain</b> (survey #1275 §3):
/// <code>
/// active TeamId (org) → membership for (NodeOperator, TeamId) → TeamRole on that edge
///                     → ICurrentUser.Roles / IAuthorizationContext.HasPermission scoped to the active org
/// </code>
/// Because the role lives on the membership edge (not on the person), the same operator can be
/// <see cref="TeamRole.Admin"/> in one org and <see cref="TeamRole.Viewer"/> in another — switching the
/// active team re-resolves the role for the now-active org. On the single-office node the operator is
/// enrolled as <see cref="TeamRole.Admin"/> of the default team (see
/// <c>MultiTeamBootstrapHostedService.EnrollOperatorAsync</c>), so they hold the full permission set.
/// </para>
/// <para>
/// <b>Synchronous resolution.</b> <see cref="ICurrentUser"/> / <see cref="IAuthorizationContext"/> are
/// synchronous contracts (ADR 0091). The in-memory membership store resolves without I/O, so the role
/// is read via the synchronous fast-path of the registry's <see cref="System.Threading.Tasks.ValueTask"/>
/// API. When the membership store becomes I/O-backed (the keystore roster doctype), this resolves the
/// role into a per-request cache populated on the active-team-changed event rather than blocking.
/// </para>
/// <para>
/// <b>Plane (card #3356).</b> This resolves the DESKTOP OPERATOR's grants, so it is no longer registered
/// as <see cref="IAuthorizationContext"/> directly: <c>NodeFinancialPostingComposition</c> registers it
/// behind the hosted app's request-scoped selected-session facade, which refuses when a hosted-web request
/// principal is bound (ADR 0160 R3-D — a web request cannot consume desktop foreground authority). It is
/// still registered as <see cref="ICurrentUser"/> unchanged.
/// </para>
/// <para>
/// <b>Scope (FLAGGED).</b> This wires the per-org role <em>resolution</em> + the primary
/// <c>ICurrentUser</c>/<c>IAuthorizationContext</c> surface. The node's loopback routes do not yet
/// <em>enforce</em> <c>HasPermission</c> at the route layer (single-operator node — survey #1275 §3:
/// "the node's loopback routes have no auth"); route-level enforcement is the follow-up that lands when
/// multi-user enrollment ships. The resolution is correct and tested now so enforcement is a wiring step,
/// not a redesign.
/// </para>
/// </remarks>
public sealed class ActiveTeamAuthorizationContext : ICurrentUser, IAuthorizationContext
{
    /// <summary>
    /// The install-constant local operator user id — the ACTOR axis (distinct from the data tenant,
    /// which is active-team-derived per ADR 0032). Ticket 194 moved it here from the deleted
    /// <c>StaticNodeUserContext</c>: that type existed only to answer one permission string with a
    /// constant, and the constant is gone. The id itself is not a grant and never was — it names who
    /// the desktop caller is, and what they may do is decided by <see cref="AuthorizationGate"/> at
    /// each act's point of use.
    /// </summary>
    public const string LocalUserId = "local";

    /// <summary>The single-office OS-user actor.</summary>
    public static readonly ActorId NodeOperator = new(LocalUserId);

    private readonly IActiveTeamAccessor _activeTeam;
    private readonly IMutableTeamRegistry _memberships;
    private readonly NodeTeamRoster? _roster;
    private readonly IOperationSigner? _nodeSigner;
    private readonly IAuthorizationClosureReader? _authorization;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Construct over the active-team accessor + the membership store, and - where the composition has them -
    /// the signed roster, this node's signer and the grant closure, which together are the ONE reading
    /// (<see cref="EffectiveMemberPermissions"/>) the web plane also answers from.
    /// </summary>
    public ActiveTeamAuthorizationContext(
        IActiveTeamAccessor activeTeam,
        IMutableTeamRegistry memberships,
        TimeProvider timeProvider,
        NodeTeamRoster? roster = null,
        IOperationSigner? nodeSigner = null,
        IAuthorizationClosureReader? authorization = null)
    {
        _activeTeam = activeTeam ?? throw new ArgumentNullException(nameof(activeTeam));
        _memberships = memberships ?? throw new ArgumentNullException(nameof(memberships));
        _roster = roster;
        _nodeSigner = nodeSigner;
        _authorization = authorization;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <inheritdoc />
    public string UserId => NodeOperator.Value;

    /// <inheritdoc />
    public IReadOnlyList<string> Roles
    {
        get
        {
            // The label is a display projection of the cached edge, and it is shown only for a caller the
            // permission answer admits - so a revoked founder loses the label on the same request they lose
            // the permissions, without the registry deciding anything.
            var role = ResolveEffectivePermissions() is null ? null : ResolveActiveRole();
            return role is null
                ? Array.Empty<string>()
                : new[] { TeamRolePermissions.DisplayName(role.Value) };
        }
    }

    /// <inheritdoc />
    public bool HasPermission(string permission)
    {
        ArgumentNullException.ThrowIfNull(permission);
        // PBAC migration (taxonomy Q6): HasPermission resolves against the active-org membership's MUTABLE
        // PERMISSION SET (the authorization truth), not the role label. The effective set is the explicit edge
        // set when present, else the default composition for the edge's role — and it emits BOTH the fine-grained
        // PBAC vocabulary AND the legacy coarse strings (ledger:post/records:* ), so existing financial-cluster
        // HasPermission("ledger:post") checks keep resolving unchanged through the migration.
        var effective = ResolveEffectivePermissions();
        return effective is not null && effective.Contains(permission);
    }

    /// <summary>
    /// The effective install-wide permission set for the desktop caller, or <c>null</c> when nothing backs
    /// them here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Ticket 290 slice 3 - the desktop plane stopped being a third decider.</b> Where the composition
    /// carries the signed roster and this node's signer, the answer comes from
    /// <see cref="EffectiveMemberPermissions"/>, the same reading <c>SelectedSessionPermissionResolver</c>
    /// gives the web plane: the LIVE roster edge for the party this node's signing key is bound to, else the
    /// grant closure, else a refusal. Membership is read on every call, so a roster revocation that converges
    /// mid-process is answered on the next request rather than at the next restart - the registry, which is
    /// refilled only at boot, cannot hold a revoked founder's authority open any more.
    /// </para>
    /// <para>
    /// The registry is a CACHE of what the boot projection wrote, and remains the answer only in a
    /// composition that registers no roster and no node signer at all - there is then no key to identify the
    /// caller by, so there is nothing for the one reading to read. Production always registers both
    /// (<c>Program.cs</c>), so that branch is the in-memory-only test composition's.
    /// </para>
    /// </remarks>
    private PermissionSet? ResolveEffectivePermissions()
    {
        var active = _activeTeam.Active;
        if (active is null)
        {
            return null;
        }

        if (_roster is null || _nodeSigner is null || _authorization is null)
        {
            return ResolveActiveMembership()?.EffectivePermissions;
        }

        var roster = _roster.Current;
        // The party this node IS, off LIVE roster state - the same key-bound reading the boot projection
        // takes (MultiTeamBootstrapHostedService.LocalLiveMember). A revoked member is not a live one.
        var local = roster.Members.FirstOrDefault(member => member.PublicKey.Equals(_nodeSigner.IssuerId));
        if (local is null)
        {
            return null;
        }

        // ICurrentUser / IAuthorizationContext are synchronous contracts (ADR 0091). For a party the roster
        // carries live, the reading answers off the in-memory roster edge and never awaits the closure, so
        // this completes synchronously; the fallback is kept for the roster-absent branch of the reading.
        var task = EffectiveMemberPermissions.ResolveAsync(
            _authorization,
            roster,
            local.PartyId,
            ActiveTeamTenantContext.ProjectTenantId(active.TeamId),
            NodeOperator,
            _timeProvider.GetUtcNow(),
            CancellationToken.None);
        return task.IsCompletedSuccessfully ? task.Result : task.AsTask().GetAwaiter().GetResult();
    }

    /// <summary>
    /// The OS-user's membership edge in the active org, or <c>null</c> when no team is active or the operator
    /// is not a member of it. Carries the effective permission set (PBAC) and the role label (display).
    /// </summary>
    private TeamMembership? ResolveActiveMembership()
    {
        var active = _activeTeam.Active;
        if (active is null)
        {
            return null;
        }

        // In-memory store resolves synchronously; read the completed ValueTask's result. Resolve the full
        // membership (not just the role) so EffectivePermissions is available.
        var task = _memberships.GetMembershipsAsync(NodeOperator);
        var memberships = task.IsCompletedSuccessfully ? task.Result : task.AsTask().GetAwaiter().GetResult();
        foreach (var m in memberships)
        {
            if (m.TeamId == active.TeamId.Value)
            {
                return m;
            }
        }
        return null;
    }

    /// <summary>
    /// The OS-user's <see cref="TeamRole"/> in the active org, or <c>null</c> when no team is active or
    /// the operator is not a member of it. Retained for the display-role projection (<see cref="Roles"/>).
    /// </summary>
    private TeamRole? ResolveActiveRole() => ResolveActiveMembership()?.Role;
}
