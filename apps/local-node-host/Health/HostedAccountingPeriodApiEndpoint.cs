using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.LocalNodeHost.Data.Financial;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// T1 local-first sweep hosted service that maps the node-local accounting-period routes
/// (list + open/close) on the shared Kestrel listener.
/// </summary>
/// <remarks>
/// <para>
/// A thin <see cref="IHostedService"/> wrapper: the route mapping lives in
/// <see cref="AccountingPeriodRoutes.Map"/> (the single source of truth shared with the route
/// tests so the wire contract has no test/prod drift). The routes are backed by
/// <see cref="NodeAccountingPeriodService"/> over <c>LocalNodeDbContext</c> (SQLCipher SC-1;
/// the existing <c>fiscal_periods</c> + <c>fiscal_years</c> tables — UPF F0, no new schema).
/// </para>
/// <para>
/// The service is injected from the OUTER host container and passed to
/// <see cref="AccountingPeriodRoutes.Map"/> as a closed-over dependency. Resolving via
/// <c>[FromServices]</c> would fail because the routes are mapped onto
/// <see cref="SharedHostedWebApp"/>'s inner <c>WebApplication</c> whose service provider does
/// NOT have the outer-container service registered (bug-2849).
/// </para>
/// <para>
/// Registration order: added to the composition root BEFORE <see cref="SharedHostedWebApp"/> so
/// its <c>StartAsync</c> maps paths while the shared app is still pre-<c>StartAsync</c>.
/// </para>
/// </remarks>
public sealed class HostedAccountingPeriodApiEndpoint : IHostedService
{
    private readonly SharedHostedWebApp _sharedApp;
    private readonly NodeAccountingPeriodService _service;
    private readonly ILogger<HostedAccountingPeriodApiEndpoint> _logger;

    /// <summary>Constructs the hosted accounting-period API endpoint.</summary>
    public HostedAccountingPeriodApiEndpoint(
        SharedHostedWebApp sharedApp,
        NodeAccountingPeriodService service,
        ILogger<HostedAccountingPeriodApiEndpoint> logger)
    {
        ArgumentNullException.ThrowIfNull(sharedApp);
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(logger);

        _sharedApp = sharedApp;
        _service = service;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sharedApp.MapApiRoutes(app => AccountingPeriodRoutes.Map(
            app.MapDeviceReachableProductDataGroup(),
            _service));

        _logger.LogInformation(
            "T1 node-local accounting-periods API registered (GET/POST {RouteBase}).",
            AccountingPeriodRoutes.RouteBase);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
