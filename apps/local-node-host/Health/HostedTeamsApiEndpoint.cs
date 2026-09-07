using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Kernel.Runtime.Teams;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Hosted service that maps the node-local teams chrome routes onto the shared
/// Kestrel listener — <c>GET /api/local-node/teams</c> (joined-team list) and
/// <c>GET /api/local-node/teams/active</c> (the current active team) that the
/// Harborline App shell reads so it renders the REAL teams (not the mock Alpha/Beta).
/// </summary>
/// <remarks>
/// <para>
/// A thin <see cref="IHostedService"/> wrapper; the route mapping lives in
/// <see cref="TeamRoutes.Map"/> (the single source of truth shared with the route
/// tests so the wire contract has no test/prod drift). The joined-team list is the
/// install-level <see cref="ITeamContextFactory.Active"/> snapshot, cross-referenced
/// with <see cref="IActiveTeamAccessor.Active"/> (for <c>isActive</c>) and the
/// per-team roster from <see cref="IMutableTeamRegistry"/> (for <c>memberCount</c>).
/// </para>
/// <para>
/// Registration order: added to the composition root BEFORE
/// <see cref="SharedHostedWebApp"/> so its <c>StartAsync</c> maps the paths while
/// the shared app is still pre-<c>StartAsync</c>.
/// </para>
/// </remarks>
public sealed class HostedTeamsApiEndpoint : IHostedService
{
    private readonly SharedHostedWebApp _sharedApp;
    private readonly ITeamContextFactory _factory;
    private readonly IActiveTeamAccessor _activeTeam;
    private readonly IMutableTeamRegistry _memberships;
    private readonly NodeCallerSessionToken _callerAuth;
    private readonly ILogger<HostedTeamsApiEndpoint> _logger;

    /// <summary>Constructs the hosted teams API endpoint.</summary>
    public HostedTeamsApiEndpoint(
        SharedHostedWebApp sharedApp,
        ITeamContextFactory factory,
        IActiveTeamAccessor activeTeam,
        IMutableTeamRegistry memberships,
        NodeCallerSessionToken callerAuth,
        ILogger<HostedTeamsApiEndpoint> logger)
    {
        ArgumentNullException.ThrowIfNull(sharedApp);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(activeTeam);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(callerAuth);
        ArgumentNullException.ThrowIfNull(logger);

        _sharedApp = sharedApp;
        _factory = factory;
        _activeTeam = activeTeam;
        _memberships = memberships;
        _callerAuth = callerAuth;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sharedApp.MapApiRoutes(app =>
            TeamRoutes.Map(
                app.MapDeviceReachableProductDataGroup(),
                _factory,
                _activeTeam,
                _memberships,
                _callerAuth));

        _logger.LogInformation(
            "Node-local teams API registered (GET {ListRoute}, GET {ActiveRoute}) — the Harborline app " +
            "teams chrome surface over the joined team contexts + active team; " +
            "inc-4 caller-auth enforced={CallerAuthEnforced}.",
            TeamRoutes.ListRouteBase, TeamRoutes.ActiveRouteBase, _callerAuth.IsEnforced);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
