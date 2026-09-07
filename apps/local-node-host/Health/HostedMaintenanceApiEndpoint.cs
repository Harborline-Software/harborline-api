using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.LocalNodeHost.Data.Maintenance;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// ADR 0115 D8 Stage 2 hosted service that maps the node-local-authoritative
/// <c>maintenance_tickets</c> read/write routes on the shared Kestrel listener.
/// </summary>
/// <remarks>
/// <para>
/// A thin <see cref="IHostedService"/> wrapper: the route mapping itself lives in
/// <see cref="MaintenanceRoutes.Map"/> (the single source of truth shared with
/// the route tests, so the wire contract has no test/prod drift). The routes are
/// backed by <see cref="NodeLocalMaintenanceDbContext"/> on the keyed
/// (SQLCipher-encrypted) store (SC-1; no plaintext path).
/// </para>
/// <para>
/// The DbContext factory is injected here from the OUTER host container and
/// passed to <see cref="MaintenanceRoutes.Map"/> as a closed-over dependency.
/// Resolving it per-request via <c>[FromServices]</c> would fail: the routes are
/// mapped onto <see cref="SharedHostedWebApp"/>'s inner <c>WebApplication</c>,
/// whose service provider does NOT have the SC-1 maintenance factory registered.
/// Capturing the outer-container factory mirrors how
/// <see cref="HostedLocalNodeApiEndpoint"/> wires its status endpoint.
/// </para>
/// <para>
/// Registration order: added to the composition root BEFORE
/// <see cref="SharedHostedWebApp"/> so its <c>StartAsync</c> maps paths while the
/// shared app is still pre-<c>StartAsync</c>.
/// </para>
/// </remarks>
public sealed class HostedMaintenanceApiEndpoint : IHostedService
{
    private readonly SharedHostedWebApp _sharedApp;
    private readonly IDbContextFactory<NodeLocalMaintenanceDbContext> _factory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<HostedMaintenanceApiEndpoint> _logger;

    /// <summary>Constructs the hosted maintenance API endpoint.</summary>
    public HostedMaintenanceApiEndpoint(
        SharedHostedWebApp sharedApp,
        IDbContextFactory<NodeLocalMaintenanceDbContext> factory,
        TimeProvider timeProvider,
        ILogger<HostedMaintenanceApiEndpoint> logger)
    {
        ArgumentNullException.ThrowIfNull(sharedApp);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(logger);

        _sharedApp = sharedApp;
        _factory = factory;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sharedApp.MapApiRoutes(app => MaintenanceRoutes.Map(
            app.MapSelectedSessionProductGroup(),
            _factory,
            _timeProvider));

        _logger.LogInformation(
            "ADR 0115 D8 Stage 2 node-local maintenance API registered on shared hosted web-app " +
            "(GET/POST {RouteBase}; GET/PATCH {RouteBase}/{{name}}).",
            MaintenanceRoutes.RouteBase, MaintenanceRoutes.RouteBase);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
