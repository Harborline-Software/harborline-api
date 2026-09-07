using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;

using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Data.People;

using Harborline.Api.Kernel.Runtime.Teams;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Hosted service that maps the node-local contacts routes onto the shared Kestrel listener
/// (T2b contacts node-flip; ADR 0113 ABSOLUTE local-first).
/// </summary>
/// <remarks>
/// <para>
/// A thin <see cref="IHostedService"/> wrapper: the route mapping itself lives in
/// <see cref="ContactRoutes.Map"/> (the single source of truth). The party repository accessor is
/// injected from the OUTER host container and passed to <see cref="ContactRoutes.Map"/> as a closed-over
/// dependency. Resolving via <c>[FromServices]</c> inside the route handlers would fail because the routes
/// are mapped onto <see cref="SharedHostedWebApp"/>'s inner <c>WebApplication</c>, whose service provider
/// does NOT have the outer-container registrations (bug-2849).
/// </para>
/// <para>
/// Registration order: added to the composition root BEFORE <see cref="SharedHostedWebApp"/> so its
/// <c>StartAsync</c> maps paths while the shared app is still pre-<c>StartAsync</c>.
/// </para>
/// </remarks>
public sealed class HostedContactApiEndpoint : IHostedService
{
    private readonly SharedHostedWebApp _sharedApp;
    private readonly IActiveTeamAccessor _activeTeam;
    private readonly NodeEfPartyRepository _parties;
    private readonly ContactCrdtProjection _crdt;
    private readonly IDbContextFactory<NodeLocalInstallationIdentityDbContext> _identityFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<HostedContactApiEndpoint> _logger;

    /// <summary>Constructs the hosted contacts API endpoint.</summary>
    public HostedContactApiEndpoint(
        SharedHostedWebApp sharedApp,
        NodeEfPartyRepository parties,
        ContactCrdtProjection crdt,
        IActiveTeamAccessor activeTeam,
        ILogger<HostedContactApiEndpoint> logger,
        IDbContextFactory<NodeLocalInstallationIdentityDbContext> identityFactory,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(sharedApp);
        ArgumentNullException.ThrowIfNull(activeTeam);
        ArgumentNullException.ThrowIfNull(parties);
        ArgumentNullException.ThrowIfNull(crdt);
        ArgumentNullException.ThrowIfNull(logger);

        _sharedApp = sharedApp;
        _activeTeam = activeTeam;
        _parties = parties;
        _crdt = crdt;
        _identityFactory = identityFactory;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sharedApp.MapApiRoutes(app => ContactRoutes.Map(
            app.MapDeviceReachableProductDataGroup(),
            _parties,
            _crdt,
            _activeTeam,
            _identityFactory,
            _timeProvider));

        _logger.LogInformation(
            "Node-local contacts API registered over the recoverable local-node store " +
            "(GET/POST {RouteBase}, GET {RouteBase}/{{id}}, POST {RouteBase}/{{id}}/update).",
            ContactRoutes.RouteBase,
            ContactRoutes.RouteBase,
            ContactRoutes.RouteBase);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
