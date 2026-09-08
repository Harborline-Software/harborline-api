using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.LocalNodeHost.Data.Leases;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// ADR 0115 D8 Stage 2 Cohort C hosted service that maps the
/// node-local-authoritative <c>leases</c> read/write routes on the shared
/// Kestrel listener.
/// </summary>
/// <remarks>
/// A thin <see cref="IHostedService"/> wrapper: the route mapping lives in
/// <see cref="LeaseRoutes.Map"/> (the single source of truth shared with the
/// route tests). Backed by <see cref="NodeLocalLeaseDbContext"/> on the keyed
/// (SQLCipher) store (SC-1; no plaintext path). The factory is injected from the
/// OUTER host container and closed over (bug-2849: do NOT use
/// <c>[FromServices]</c>). Mirrors <see cref="HostedMaintenanceApiEndpoint"/>.
/// </remarks>
public sealed class HostedLeaseApiEndpoint : IHostedService
{
    private readonly SharedHostedWebApp _sharedApp;
    private readonly IDbContextFactory<NodeLocalLeaseDbContext> _factory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<HostedLeaseApiEndpoint> _logger;

    /// <summary>Constructs the hosted lease API endpoint.</summary>
    public HostedLeaseApiEndpoint(
        SharedHostedWebApp sharedApp,
        IDbContextFactory<NodeLocalLeaseDbContext> factory,
        TimeProvider timeProvider,
        ILogger<HostedLeaseApiEndpoint> logger)
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
        _sharedApp.MapApiRoutes(app => LeaseRoutes.Map(
            app.MapDeviceReachableProductDataGroup(),
            _factory,
            _timeProvider));

        _logger.LogInformation(
            "ADR 0115 D8 Stage 2 Cohort C node-local lease API registered on shared hosted web-app " +
            "(GET/POST {RouteBase}; GET {RouteBase}/{{name}}).",
            LeaseRoutes.RouteBase, LeaseRoutes.RouteBase);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
