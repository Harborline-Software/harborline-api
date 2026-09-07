using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.LocalNodeHost.Data.Roster;
using Harborline.Api.LocalNodeHost.Data.Search;

namespace Harborline.Api.LocalNodeHost.Data.Identity;

/// <summary>Resolves the effective PBAC set for one selected-session principal.</summary>
internal interface ISelectedSessionPermissionResolver
{
    ValueTask<PermissionSet?> ResolveAsync(
        SelectedSessionRequestPrincipal principal,
        CancellationToken cancellationToken = default);
}

/// <summary>Reads the grant authority's freshness fence for a principal.</summary>
internal interface ISelectedSessionAuthorizationEpochReader
{
    Task<long?> ReadAsync(
        TenantId tenantId,
        string principalId,
        CancellationToken cancellationToken = default);
}

/// <summary>Durable authorization-epoch reader over the node's grant store database.</summary>
internal sealed class NodeSelectedSessionAuthorizationEpochReader : ISelectedSessionAuthorizationEpochReader
{
    private readonly IDbContextFactory<NodeLocalSearchDbContext> _contextFactory;

    public NodeSelectedSessionAuthorizationEpochReader(
        IDbContextFactory<NodeLocalSearchDbContext> contextFactory)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
    }

    public async Task<long?> ReadAsync(
        TenantId tenantId,
        string principalId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        var row = await context.GrantAuthorizationEpochs
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.TenantId == tenantId.Value && item.PrincipalId == principalId,
                cancellationToken)
            .ConfigureAwait(false);

        return row?.AuthorizationEpoch;
    }
}

/// <summary>
/// Selected-session PEP. The signed roster edge is authoritative when present. A browser invitee can be
/// authenticated before its deferred atlas admission exists, so the exact live pinned grant is the
/// authority source for that one transition state; it is never a client-side composition or fallback role.
/// </summary>
internal sealed class SelectedSessionPermissionResolver : ISelectedSessionPermissionResolver
{
    private readonly AuthorizationGate _gate;
    private readonly AuthorizationRefusalAudit? _refusalAudit;
    private readonly IVerifiedTenantRosterReader _rosterReader;
    private readonly IGrantStore _grantStore;
    private readonly IAuthorizationClosureReader _authorization;
    private readonly ISelectedSessionAuthorizationEpochReader _epochReader;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SelectedSessionPermissionResolver> _logger;

    public SelectedSessionPermissionResolver(
        IVerifiedTenantRosterReader rosterReader,
        IGrantStore grantStore,
        IAuthorizationClosureReader authorization,
        ISelectedSessionAuthorizationEpochReader epochReader,
        TimeProvider timeProvider,
        ILogger<SelectedSessionPermissionResolver> logger,
        AuthorizationGate gate,
        AuthorizationRefusalAudit? refusalAudit = null)
    {
        _gate = gate;
        _refusalAudit = refusalAudit;
        _rosterReader = rosterReader ?? throw new ArgumentNullException(nameof(rosterReader));
        _grantStore = grantStore ?? throw new ArgumentNullException(nameof(grantStore));
        _authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        _epochReader = epochReader ?? throw new ArgumentNullException(nameof(epochReader));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async ValueTask<PermissionSet?> ResolveAsync(
        SelectedSessionRequestPrincipal principal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);

        try
        {
            var epoch = await _epochReader
                .ReadAsync(principal.TenantId, principal.PrincipalUserId.Value, cancellationToken)
                .ConfigureAwait(false);
            if (epoch is null || epoch.Value != principal.AuthorizationEpoch)
            {
                return null;
            }

            // Check the exact pinned grant on every request; this remains fail-closed even if a bad
            // writer failed to advance the epoch. Its bundle is used only when deferred admission has
            // not produced a signed roster edge yet.
            var liveGrant = await FindLivePinnedGrantAsync(principal, cancellationToken)
                .ConfigureAwait(false);
            if (liveGrant is null)
            {
                return null;
            }

            // Do not cache this value by epoch alone. Roster CRDT sync can narrow or eject a member
            // without touching the grant store's authorization epoch, so each request reads the signed
            // roster edge directly. A grant bundle is only the honest authority for a web invitee whose
            // atlas edge is deliberately deferred until device enrollment.
            var roster = await _rosterReader
                .ReadAsync(principal.TenantId, cancellationToken)
                .ConfigureAwait(false);
            // Derive the live inputs once; the gate decides each permission projected into this session.
            var inputs = await EffectiveMemberPermissions.ReadAsync(
                _authorization,
                roster,
                principal.CanonicalParty.Value,
                principal.TenantId,
                NodeGatePrincipal.Of(principal),
                _timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
            var allowed = new List<string>();
            var authority = new AuthorizationWriteContext(NodeGatePrincipal.Of(principal), principal.TenantId,
                _timeProvider.GetUtcNow());
            foreach (var permission in (inputs.Permissions ?? PermissionSet.Empty).Permissions
                .Append(TeamRolePermissions.MembersManage).Distinct(StringComparer.Ordinal))
            {
                var operation = AuthorizationOperation.Parse(permission);
                var decision = await _gate.DecideAsync(authority.Request(operation,
                    AuthorizationGate.RecordKindFor(operation), "session") with { Roster = inputs }, cancellationToken)
                    .ConfigureAwait(false);
                if (_refusalAudit is not null) await _refusalAudit.RecordAsync(decision, cancellationToken).ConfigureAwait(false);
                if (decision.Verdict == AuthorizationVerdict.Allowed) allowed.Add(permission);
            }
            var permissions = allowed.Count == 0 ? null : PermissionSet.From(allowed);

            // Close the read-side race: if a grant mutation landed while the roster was loading, do
            // not publish the pre-bump roster set into this request.
            var finalEpoch = await _epochReader
                .ReadAsync(principal.TenantId, principal.PrincipalUserId.Value, cancellationToken)
                .ConfigureAwait(false);
            if (finalEpoch is null || finalEpoch.Value != principal.AuthorizationEpoch ||
                finalEpoch.Value != epoch.Value)
            {
                return null;
            }

            return permissions;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Authorization resolution is a security boundary. A broken roster, grant store, epoch
            // read, or malformed coordinate is indistinguishable from no permission here.
            _logger.LogError(
                exception,
                "Selected-session permission resolution fault for tenant {TenantId}, principal {PrincipalId}, "
                + "party {PartyId}.",
                principal.TenantId.Value,
                principal.PrincipalUserId.Value,
                principal.CanonicalParty.Value);
            return null;
        }
    }

    private async Task<AccessGrant?> FindLivePinnedGrantAsync(
        SelectedSessionRequestPrincipal principal,
        CancellationToken cancellationToken)
    {
        var pin = principal.PinnedGrantOwnerVersions.SingleOrDefault();
        if (pin is null || string.IsNullOrWhiteSpace(pin.GrantId))
        {
            return null;
        }

        var actor = NodeGatePrincipal.Of(principal);
        var grants = await _grantStore
            .FindByPrincipalAsync(principal.TenantId, actor, cancellationToken)
            .ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();
        return grants.SingleOrDefault(grant =>
            grant.Subject.Equals(actor) &&
            string.Equals(grant.GrantId.ToString(), pin.GrantId, StringComparison.Ordinal) &&
            grant.IsActiveAt(now));
    }
}

/// <summary>Explicit fail-closed resolver for minimal inner-host test compositions.</summary>
internal sealed class FailClosedSelectedSessionPermissionResolver : ISelectedSessionPermissionResolver
{
    public ValueTask<PermissionSet?> ResolveAsync(
        SelectedSessionRequestPrincipal principal,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<PermissionSet?>(null);
}
