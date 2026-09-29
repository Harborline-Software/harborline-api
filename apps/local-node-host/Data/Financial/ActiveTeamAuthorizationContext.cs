using System;
using System.Collections.Generic;
using System.Linq;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Enrollment;
using Harborline.Api.LocalNodeHost.Health;

namespace Harborline.Api.LocalNodeHost.Data.Financial;

/// <summary>
/// Desktop operator identity and active-tenant permission checks. The gate decides each act from
/// durable roster and grant inputs; the boot registry supplies membership evidence and display labels.
/// </summary>
/// <remarks>The selected-session facade fences this context from hosted-web principals.
/// Synchronous identity contracts bridge the asynchronous gate without caching permission verdicts.</remarks>
public sealed class ActiveTeamAuthorizationContext : ICurrentUser, IAuthorizationContext
{
    private readonly IActiveTeamAccessor _activeTeam;
    private readonly IMutableTeamRegistry _memberships;
    private readonly NodeTeamRoster? _roster;
    private readonly IOperationSigner? _nodeSigner;
    private readonly TimeProvider _timeProvider;
    private readonly AuthorizationGate _gate;
    private readonly AuthorizationRefusalAudit? _refusalAudit;
    private readonly NodeOperatorIdentity? _nodeOperator;

    /// <summary>
    /// Construct over the active-team accessor + the membership store, and - where the composition has them -
    /// the signed roster and this node's signer. The gate reads the grant closure it owns.
    /// </summary>
    public ActiveTeamAuthorizationContext(
        IActiveTeamAccessor activeTeam,
        IMutableTeamRegistry memberships,
        TimeProvider timeProvider,
        NodeTeamRoster? roster = null,
        IOperationSigner? nodeSigner = null,
        AuthorizationGate? gate = null,
        AuthorizationRefusalAudit? refusalAudit = null,
        NodeOperatorIdentity? nodeOperator = null)
    {
        _nodeOperator = nodeOperator;
        _activeTeam = activeTeam ?? throw new ArgumentNullException(nameof(activeTeam));
        _memberships = memberships ?? throw new ArgumentNullException(nameof(memberships));
        _roster = roster;
        _nodeSigner = nodeSigner;
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _refusalAudit = refusalAudit;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>
    /// The desktop caller (ticket 294 slice 3b): the roster party bound to this node's signing key, which on the
    /// founding node is the founder's canonical tenant principal, the key the grant store and the web plane use.
    /// It is not a grant; what it may do is decided by <see cref="AuthorizationGate"/> at each act's point of use.
    /// Null when the composition has no roster or the key holds no edge, and then every act is refused.
    /// </summary>
    private ActorId? Operator =>
        _nodeOperator?.Principal ?? new NodeOperatorIdentity(_roster, _nodeSigner).Principal;

    /// <inheritdoc />
    /// <remarks>Empty when there is no desktop actor; nothing is attributed to a constant in its place.</remarks>
    public string UserId => Operator?.Value ?? string.Empty;

    /// <inheritdoc />
    public IReadOnlyList<string> Roles
    {
        get
        {
            // The label is a display projection of the cached edge, and it is shown only for a caller the
            // permission answer admits - so a revoked founder loses the label on the same request they lose
            // the permissions, without the registry deciding anything.
            var role = Decide(null)?.Verdict == AuthorizationVerdict.Allowed ? ResolveActiveRole() : null;
            return role is null
                ? Array.Empty<string>()
                : new[] { TeamRolePermissions.DisplayName(role.Value) };
        }
    }

    /// <inheritdoc />
    public bool HasPermission(string permission)
    {
        return TryParsePermission(permission, out _)
            && Decide(permission)?.Verdict == AuthorizationVerdict.Allowed;
    }

    // The synchronous IAuthorizationContext.HasPermission contract (called by
    // SelectedSessionTenantContext.HasPermission) and ICurrentUser.Roles (read by
    // HostedFormsApiEndpoint.StartAsync) force this single bridge. Async callers use
    // DecideAsync directly; all awaited gate/audit work below avoids capturing a context.
    private AuthorizationDecision? Decide(string? permission)
    {
        var pending = DecideAsync(permission);
        return pending.IsCompletedSuccessfully ? pending.Result : pending.AsTask().GetAwaiter().GetResult();
    }

    // Carries the same decision into the existing refusal renderer and audit sink. No verdict is cached.
    internal async ValueTask<AuthorizationDecision?> DecideAsync(string? permission)
    {
        var active = _activeTeam.Active;
        if (active is null || Operator is not { } actor) return null;
        var tenant = ActiveTeamTenantContext.ProjectTenantId(active.TeamId);
        var at = _timeProvider.GetUtcNow();
        // The gate derives the actor's roster facts through its own constraint reader (T-519).
        AuthorizationDecision? decision = null;
        // A role label previously required at least one allowed act. Each candidate still asks the gate;
        // an empty input set asks it once as well, so absence has refusal evidence.
        var candidates = permission is null
            ? (await _gate.InstallRootPermissionsAsync(actor, tenant, at, CancellationToken.None)
                .ConfigureAwait(false)).Permissions.DefaultIfEmpty(TeamRolePermissions.RecordsRead)
            : [permission];
        foreach (var candidate in candidates)
        {
            if (!TryParsePermission(candidate, out var operation)) continue;
            decision = await _gate.DecideAsync(new AuthorizationWriteContext(actor, tenant, at)
                .Request(operation, AuthorizationGate.RecordKindFor(operation), "desktop")).ConfigureAwait(false);
            if (permission is not null && _refusalAudit is not null)
                await _refusalAudit.RecordAsync(decision, CancellationToken.None).ConfigureAwait(false);
            if (decision.Verdict == AuthorizationVerdict.Allowed) break;
        }
        return decision;
    }

    private static bool TryParsePermission(string? permission, out AuthorizationOperation operation)
    {
        try
        {
            operation = AuthorizationOperation.Parse(permission!);
            return true;
        }
        catch (ArgumentException)
        {
            operation = default;
            return false;
        }
    }

    /// <summary>
    /// The OS-user's membership edge in the active org, or <c>null</c> when no team is active or the operator
    /// is not a member of it. Carries boot membership metadata and the role label for display only.
    /// </summary>
    private TeamMembership? ResolveActiveMembership(ActorId actor)
    {
        var active = _activeTeam.Active;
        if (active is null)
        {
            return null;
        }

        // The registry is read only for membership evidence and display metadata.
        var task = _memberships.GetMembershipsAsync(actor);
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
    private TeamRole? ResolveActiveRole() => Operator is { } actor ? ResolveActiveMembership(actor)?.Role : null;
}
