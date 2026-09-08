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
    private readonly AuthorizationGate _gate;
    private readonly AuthorizationRefusalAudit? _refusalAudit;

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
        IAuthorizationClosureReader? authorization = null,
        AuthorizationGate? gate = null,
        AuthorizationRefusalAudit? refusalAudit = null)
    {
        _activeTeam = activeTeam ?? throw new ArgumentNullException(nameof(activeTeam));
        _memberships = memberships ?? throw new ArgumentNullException(nameof(memberships));
        _roster = roster;
        _nodeSigner = nodeSigner;
        _authorization = authorization;
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _refusalAudit = refusalAudit;
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
            var role = Decide(null)?.Verdict == AuthorizationVerdict.Allowed ? ResolveActiveRole() : null;
            return role is null
                ? Array.Empty<string>()
                : new[] { TeamRolePermissions.DisplayName(role.Value) };
        }
    }

    /// <inheritdoc />
    public bool HasPermission(string permission)
    {
        ArgumentNullException.ThrowIfNull(permission);
        return Decide(permission)?.Verdict == AuthorizationVerdict.Allowed;
    }

    private AuthorizationDecision? Decide(string? permission)
    {
        var pending = DecideAsync(permission);
        return pending.IsCompletedSuccessfully ? pending.Result : pending.AsTask().GetAwaiter().GetResult();
    }

    // Carries the same decision into the existing refusal renderer and audit sink. No verdict is cached.
    internal async ValueTask<AuthorizationDecision?> DecideAsync(string? permission)
    {
        var active = _activeTeam.Active;
        if (active is null) return null;
        var tenant = ActiveTeamTenantContext.ProjectTenantId(active.TeamId);
        var at = _timeProvider.GetUtcNow();
        var membership = ResolveActiveMembership();
        var inputs = new AuthorizationRosterInputs(NodeOperator.Value, false, false, null);
        if (_roster is not null && _nodeSigner is not null && _authorization is not null)
        {
            var roster = _roster.Current;
            // Admissions retain revoked keys, so their ejection reaches the gate as evidence too.
            var partyId = roster.Members.FirstOrDefault(member => member.PublicKey.Equals(_nodeSigner.IssuerId))?.PartyId
                ?? roster.EnumerateAdmissions().FirstOrDefault(member => member.PublicKey.Equals(_nodeSigner.IssuerId))?.PartyId;
            if (partyId is not null)
                inputs = await EffectiveMemberPermissions.ReadAsync(_authorization, roster, partyId,
                    tenant, NodeOperator, at, CancellationToken.None).ConfigureAwait(false);
        }
        AuthorizationDecision? decision = null;
        // A role label previously required at least one allowed act. Each candidate still asks the gate;
        // an empty input set asks it once as well, so absence has refusal evidence.
        var candidates = permission is null
            ? (inputs.Permissions ?? PermissionSet.Empty).Permissions.DefaultIfEmpty(TeamRolePermissions.RecordsRead)
            : [permission];
        foreach (var candidate in candidates)
        {
            var operation = AuthorizationOperation.Parse(candidate);
            decision = await _gate.DecideAsync(new AuthorizationWriteContext(NodeOperator, tenant, at)
                .Request(operation, AuthorizationGate.RecordKindFor(operation), "desktop") with
                {
                    Roster = inputs with { RegistryMember = membership is not null }
                }).ConfigureAwait(false);
            if (_refusalAudit is not null)
                await _refusalAudit.RecordAsync(decision, CancellationToken.None).ConfigureAwait(false);
            if (decision.Verdict == AuthorizationVerdict.Allowed) break;
        }
        return decision;
    }

    /// <summary>
    /// The OS-user's membership edge in the active org, or <c>null</c> when no team is active or the operator
    /// is not a member of it. Carries boot membership metadata and the role label for display only.
    /// </summary>
    private TeamMembership? ResolveActiveMembership()
    {
        var active = _activeTeam.Active;
        if (active is null)
        {
            return null;
        }

        // The registry is read only for membership evidence and display metadata.
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
