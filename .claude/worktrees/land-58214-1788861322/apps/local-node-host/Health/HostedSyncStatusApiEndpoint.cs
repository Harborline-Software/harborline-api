using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.Kernel.Runtime.Teams;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Hosted service that maps the node-local sync-status route onto the shared
/// Kestrel listener (Phase A — sync-status-read-model survey 2026-06-19; PAO
/// design 2026-06-19). Exposes <c>GET /api/local-node/sync-status</c> — the
/// four-state multi-device sync status the Harborline App UI reads.
/// </summary>
/// <remarks>
/// <para>
/// A thin <see cref="IHostedService"/> wrapper; the route mapping lives in
/// <see cref="SyncStatusRoutes.Map"/> (the single source of truth shared with
/// the route tests so the wire contract has no test/prod drift). The
/// sync-status read-model is TEAM-SCOPED (it lives in the active team's child
/// container alongside the gossip daemon), so unlike the financial-cluster
/// endpoints this one closes over the install-level
/// <see cref="IActiveTeamAccessor"/> and the route resolves the read-model
/// per-request from the active team (the <see cref="LocalNodeHealthCheck"/>
/// precedent), NOT a closed-over outer-container read-model.
/// </para>
/// <para>
/// Registration order: added to the composition root BEFORE
/// <see cref="SharedHostedWebApp"/> so its <c>StartAsync</c> maps the path while
/// the shared app is still pre-<c>StartAsync</c>.
/// </para>
/// </remarks>
public sealed class HostedSyncStatusApiEndpoint : IHostedService
{
    private readonly SharedHostedWebApp _sharedApp;
    private readonly IActiveTeamAccessor _activeTeam;
    private readonly NodeCallerSessionToken _callerAuth;
    private readonly TimeProvider _timeProvider;
    private readonly IEnrollmentRebindStatus? _rebindStatus;
    private readonly ILogger<HostedSyncStatusApiEndpoint> _logger;

    /// <summary>Constructs the hosted sync-status API endpoint.</summary>
    /// <remarks>
    /// MAJOR-1 (cerebrum [2026-06-21] verdict): <paramref name="rebindStatus"/> (the
    /// <see cref="LocalNodeWorker"/>, resolved as <see cref="IEnrollmentRebindStatus"/>) lets the route surface a
    /// FAULTED daemon-rebind on the sync-status payload — making the silent half-rebound DETECTABLE. Optional so a
    /// composition that wired no joiner role still maps the route (healthy enrollment default).
    /// </remarks>
    public HostedSyncStatusApiEndpoint(
        SharedHostedWebApp sharedApp,
        IActiveTeamAccessor activeTeam,
        NodeCallerSessionToken callerAuth,
        TimeProvider timeProvider,
        ILogger<HostedSyncStatusApiEndpoint> logger,
        IEnrollmentRebindStatus? rebindStatus = null)
    {
        ArgumentNullException.ThrowIfNull(sharedApp);
        ArgumentNullException.ThrowIfNull(activeTeam);
        ArgumentNullException.ThrowIfNull(callerAuth);
        ArgumentNullException.ThrowIfNull(logger);

        _sharedApp = sharedApp;
        _activeTeam = activeTeam;
        _callerAuth = callerAuth;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _rebindStatus = rebindStatus;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sharedApp.MapApiRoutes(app => SyncStatusRoutes.Map(
            app.MapDeviceReachableProductDataGroup(),
            _activeTeam,
            _callerAuth,
            _timeProvider,
            _rebindStatus));

        _logger.LogInformation(
            "Node-local sync-status API registered (GET {RouteBase}) — four-state " +
            "multi-device sync surface over the active team's gossip daemon (Phase A); " +
            "inc-4 caller-auth enforced={CallerAuthEnforced}.",
            SyncStatusRoutes.RouteBase, _callerAuth.IsEnforced);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
