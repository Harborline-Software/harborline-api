using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.LocalNodeHost.Data.Financial;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// T1 local-first sweep hosted service that maps the node-local accounting-summary routes
/// (dashboard GL aggregation + outstanding invoices) on the shared Kestrel listener.
/// </summary>
/// <remarks>
/// <para>
/// A thin <see cref="IHostedService"/> wrapper: the route mapping lives in
/// <see cref="AccountingSummaryRoutes.Map"/> (the single source of truth shared with the route
/// tests). The routes are backed by <see cref="NodeAccountingSummaryService"/>, a read-only
/// projection over <c>LocalNodeDbContext</c> (the now-node-resident GL + AR data).
/// </para>
/// <para>
/// The service is injected from the OUTER host container and passed to
/// <see cref="AccountingSummaryRoutes.Map"/> as a closed-over dependency (bug-2849 — NOT
/// <c>[FromServices]</c> on the inner shared-app container). Registered before
/// <see cref="SharedHostedWebApp"/> so paths are mapped before Kestrel starts.
/// </para>
/// </remarks>
public sealed class HostedAccountingSummaryApiEndpoint : IHostedService
{
    private readonly SharedHostedWebApp _sharedApp;
    private readonly NodeAccountingSummaryService _service;
    private readonly ILogger<HostedAccountingSummaryApiEndpoint> _logger;

    /// <summary>Constructs the hosted accounting-summary API endpoint.</summary>
    public HostedAccountingSummaryApiEndpoint(
        SharedHostedWebApp sharedApp,
        NodeAccountingSummaryService service,
        ILogger<HostedAccountingSummaryApiEndpoint> logger)
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
        _sharedApp.MapApiRoutes(app => AccountingSummaryRoutes.Map(
            app.MapDeviceReachableProductDataGroup(),
            _service));

        _logger.LogInformation(
            "T1 node-local accounting-summary API registered (GET {RouteBase}/summary + /outstanding).",
            AccountingSummaryRoutes.RouteBase);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
