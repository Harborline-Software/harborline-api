using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.Blocks.FinancialAr.Services;

using Harborline.Api.LocalNodeHost.Data.Financial;

using Harborline.Api.Kernel.Runtime.Teams;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Hosted service that maps the node-local recurring-invoice routes onto the shared Kestrel listener
/// (T2b recurring-invoice node-flip; ADR 0113 ABSOLUTE local-first).
/// </summary>
/// <remarks>
/// <para>
/// A thin <see cref="IHostedService"/> wrapper: the route mapping itself lives in
/// <see cref="RecurringInvoiceRoutes.Map"/> (the single source of truth shared with any route tests so the
/// wire contract has no test/prod drift). The schedule store + RRULE engine + draft → issue → JE flow are
/// all node-resident (recoverable <c>local-node.db</c>); the service accessor is injected from the OUTER
/// host container and passed to <see cref="RecurringInvoiceRoutes.Map"/> as a closed-over dependency.
/// Resolving via <c>[FromServices]</c> inside the route handlers would fail because the routes are mapped
/// onto <see cref="SharedHostedWebApp"/>'s inner <c>WebApplication</c>, whose service provider does NOT have
/// the outer-container registrations (bug-2849).
/// </para>
/// <para>
/// Registration order: added to the composition root BEFORE <see cref="SharedHostedWebApp"/> so its
/// <c>StartAsync</c> maps paths while the shared app is still pre-<c>StartAsync</c>.
/// </para>
/// </remarks>
public sealed class HostedRecurringInvoiceApiEndpoint : IHostedService
{
    private readonly SharedHostedWebApp _sharedApp;
    private readonly IActiveTeamAccessor _activeTeam;
    private readonly IRecurringInvoiceService _recurring;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<HostedRecurringInvoiceApiEndpoint> _logger;

    /// <summary>Constructs the hosted recurring-invoice API endpoint.</summary>
    public HostedRecurringInvoiceApiEndpoint(
        SharedHostedWebApp sharedApp,
        IRecurringInvoiceService recurring,
        IActiveTeamAccessor activeTeam,
        TimeProvider timeProvider,
        ILogger<HostedRecurringInvoiceApiEndpoint> logger)
    {
        ArgumentNullException.ThrowIfNull(sharedApp);
        ArgumentNullException.ThrowIfNull(activeTeam);
        ArgumentNullException.ThrowIfNull(recurring);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _sharedApp = sharedApp;
        _activeTeam = activeTeam;
        _recurring = recurring;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sharedApp.MapApiRoutes(app => RecurringInvoiceRoutes.Map(
            app.MapDeviceReachableProductDataGroup(),
            _recurring,
            _activeTeam,
            _timeProvider));

        _logger.LogInformation(
            "Node-local recurring-invoices API registered over the recoverable local-node store " +
            "(GET/POST {RouteBase}, GET {RouteBase}/{{id}}, POST {RouteBase}/{{id}}/[pause|resume|archive|generate]).",
            RecurringInvoiceRoutes.RouteBase,
            RecurringInvoiceRoutes.RouteBase,
            RecurringInvoiceRoutes.RouteBase);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
