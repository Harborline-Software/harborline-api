using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.LocalNodeHost.Data.Properties;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// ADR 0115 D8 Stage 2 Cohort C hosted service that maps the
/// node-local-authoritative <c>properties</c> read/write routes on the shared
/// Kestrel listener.
/// </summary>
/// <remarks>
/// <para>
/// A thin <see cref="IHostedService"/> wrapper: the route mapping lives in
/// <see cref="PropertyRoutes.Map"/> (the single source of truth shared with the
/// route tests so the wire contract has no test/prod drift). Backed by
/// <see cref="NodeLocalPropertyDbContext"/> on the keyed (SQLCipher) store (SC-1;
/// no plaintext path).
/// </para>
/// <para>
/// The DbContext factory is injected here from the OUTER host container and
/// passed to <see cref="PropertyRoutes.Map"/> as a closed-over dependency.
/// Resolving it per-request via <c>[FromServices]</c> would fail (bug-2849): the
/// routes are mapped onto <see cref="SharedHostedWebApp"/>'s inner
/// <c>WebApplication</c>, whose service provider does NOT have the SC-1 factory
/// registered. Mirrors <see cref="HostedMaintenanceApiEndpoint"/>.
/// </para>
/// </remarks>
public sealed class HostedPropertyApiEndpoint : IHostedService
{
    private readonly SharedHostedWebApp _sharedApp;
    private readonly IDbContextFactory<NodeLocalPropertyDbContext> _factory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<HostedPropertyApiEndpoint> _logger;

    /// <summary>Constructs the hosted property API endpoint.</summary>
    public HostedPropertyApiEndpoint(
        SharedHostedWebApp sharedApp,
        IDbContextFactory<NodeLocalPropertyDbContext> factory,
        TimeProvider timeProvider,
        ILogger<HostedPropertyApiEndpoint> logger)
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
        _sharedApp.MapApiRoutes(app => PropertyRoutes.Map(
            app.MapDeviceReachableProductDataGroup(),
            _factory,
            _timeProvider));

        _logger.LogInformation(
            "ADR 0115 D8 Stage 2 Cohort C node-local property API registered on shared hosted web-app " +
            "(GET/POST {RouteBase}; GET {RouteBase}/{{name}}).",
            PropertyRoutes.RouteBase, PropertyRoutes.RouteBase);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
