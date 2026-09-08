using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Entities;

using Harborline.Api.Kernel.Runtime.Teams;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// ADR 0115 gap C4 hosted service that maps the node-local-authoritative
/// <c>legal_entities</c> read/write routes on the shared Kestrel listener.
/// </summary>
/// <remarks>
/// <para>
/// A thin <see cref="IHostedService"/> wrapper: the route mapping itself lives
/// in <see cref="EntityRoutes.Map"/> (the single source of truth shared with
/// the route tests so the wire contract has no test/prod drift). The routes are
/// backed by <see cref="LocalNodeDbContext"/> (the shared-module financial
/// context) on the SQLCipher-encrypted store (SC-1; no plaintext path).
/// </para>
/// <para>
/// The DbContext factory is injected from the OUTER host container and passed
/// to <see cref="EntityRoutes.Map"/> as a closed-over dependency. Resolving via
/// <c>[FromServices]</c> inside the route handler would fail because the routes
/// are mapped onto <see cref="SharedHostedWebApp"/>'s inner
/// <c>WebApplication</c>, whose service provider does NOT have the
/// outer-container factory registered (bug-2849).
/// </para>
/// <para>
/// Registration order: added to the composition root BEFORE
/// <see cref="SharedHostedWebApp"/> so its <c>StartAsync</c> maps paths while
/// the shared app is still pre-<c>StartAsync</c>.
/// </para>
/// </remarks>
public sealed class HostedEntityApiEndpoint : IHostedService
{
    private readonly SharedHostedWebApp _sharedApp;
    private readonly IActiveTeamAccessor _activeTeam;
    private readonly IDbContextFactory<LocalNodeDbContext> _factory;
    private readonly NodeEntityWriter _writer;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<HostedEntityApiEndpoint> _logger;

    /// <summary>Constructs the hosted entity API endpoint. The registered pre-commit
    /// <see cref="IEntityValidator"/> is optional for back-compatible embedders (ticket 151).</summary>
    public HostedEntityApiEndpoint(
        SharedHostedWebApp sharedApp,
        IDbContextFactory<LocalNodeDbContext> factory,
        IActiveTeamAccessor activeTeam,
        ILogger<HostedEntityApiEndpoint> logger,
        NodeEntityWriter writer,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(sharedApp);
        ArgumentNullException.ThrowIfNull(activeTeam);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(logger);

        _sharedApp = sharedApp;
        _activeTeam = activeTeam;
        _factory = factory;
        _writer = writer;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sharedApp.MapApiRoutes(app => EntityRoutes.Map(
            app.MapDeviceReachableProductDataGroup(),
            _factory,
            _activeTeam,
            _writer,
            _timeProvider));

        _logger.LogInformation(
            "ADR 0115 gap C4 node-local entity API registered " +
            "(GET/POST {RouteBase}).",
            EntityRoutes.RouteBase);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
