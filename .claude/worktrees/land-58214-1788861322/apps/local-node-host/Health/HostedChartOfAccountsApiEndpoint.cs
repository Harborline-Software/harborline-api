using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.LocalNodeHost.Data;

using Harborline.Api.Kernel.Runtime.Teams;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// ADR 0115 gap C4 hosted service that maps the node-local-authoritative
/// <c>chart-of-accounts</c> seed route on the shared Kestrel listener.
/// </summary>
/// <remarks>
/// <para>
/// A thin <see cref="IHostedService"/> wrapper: the route mapping itself lives
/// in <see cref="ChartOfAccountsRoutes.Map"/> (the single source of truth shared
/// with the route tests so the wire contract has no test/prod drift). The route is
/// backed by <see cref="LocalNodeDbContext"/> (the shared-module financial context)
/// on the SQLCipher-encrypted store (SC-1; no plaintext path).
/// </para>
/// <para>
/// The DbContext factory is injected from the OUTER host container and passed
/// to <see cref="ChartOfAccountsRoutes.Map"/> as a closed-over dependency.
/// Resolving via <c>[FromServices]</c> inside the route handler would fail because
/// the routes are mapped onto <see cref="SharedHostedWebApp"/>'s inner
/// <c>WebApplication</c>, whose service provider does NOT have the
/// outer-container factory registered (bug-2849).
/// </para>
/// <para>
/// Registration order: added to the composition root BEFORE
/// <see cref="SharedHostedWebApp"/> so its <c>StartAsync</c> maps paths while
/// the shared app is still pre-<c>StartAsync</c>.
/// </para>
/// </remarks>
public sealed class HostedChartOfAccountsApiEndpoint : IHostedService
{
    private readonly SharedHostedWebApp _sharedApp;
    private readonly IActiveTeamAccessor _activeTeam;
    private readonly IDbContextFactory<LocalNodeDbContext> _factory;
    private readonly TimeProvider _time;
    private readonly ILogger<HostedChartOfAccountsApiEndpoint> _logger;

    /// <summary>Constructs the hosted chart-of-accounts API endpoint.</summary>
    public HostedChartOfAccountsApiEndpoint(
        SharedHostedWebApp sharedApp,
        IDbContextFactory<LocalNodeDbContext> factory,
        IActiveTeamAccessor activeTeam,
        TimeProvider time,
        ILogger<HostedChartOfAccountsApiEndpoint> logger)
    {
        ArgumentNullException.ThrowIfNull(sharedApp);
        ArgumentNullException.ThrowIfNull(activeTeam);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(logger);

        _sharedApp = sharedApp;
        _activeTeam = activeTeam;
        _factory = factory;
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sharedApp.MapApiRoutes(app => ChartOfAccountsRoutes.Map(
            app.MapDeviceReachableProductDataGroup(),
            _factory,
            _activeTeam,
            _time));
        // T1 local-first sweep — node-local COA management surface (list/detail/create/archive),
        // relocating the Bridge /api/v1/chart-of-accounts read+write off signal-bridge so the
        // Accounting module + the JE Account column render offline.
        _sharedApp.MapApiRoutes(app => ChartOfAccountsManagementRoutes.Map(
            app.MapDeviceReachableProductDataGroup(),
            _factory,
            _time));

        _logger.LogInformation(
            "ADR 0115 gap C4 node-local chart-of-accounts seed API registered (POST {SeedRoute}); " +
            "T1 node-local chart-of-accounts management API registered (GET/POST {RouteBase}).",
            ChartOfAccountsRoutes.SeedRoute,
            ChartOfAccountsManagementRoutes.RouteBase);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
