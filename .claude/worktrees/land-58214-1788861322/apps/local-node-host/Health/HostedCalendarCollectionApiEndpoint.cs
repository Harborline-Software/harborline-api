using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.Blocks.Calendar.Services;
using Harborline.Api.Kernel.Runtime.Teams;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Hosted service that maps the node-local owned-CALENDAR collection routes (list + create) onto the
/// shared Kestrel listener (calendar productization #149, slice C1).
/// </summary>
/// <remarks>
/// <para>
/// A thin <see cref="IHostedService"/> wrapper: the mapping lives in
/// <see cref="CalendarCollectionRoutes.Map"/> (the single source of truth shared with the route tests,
/// no test/prod wire drift). The calendar store + active-team accessor + caller-auth guard are resolved
/// from the OUTER host container and passed as closed-over dependencies (resolving via
/// <c>[FromServices]</c> inside handlers would fail on the inner <c>WebApplication</c> — bug-2849).
/// </para>
/// <para>
/// Registration order: added to the composition root BEFORE <see cref="SharedHostedWebApp"/> so its
/// <c>StartAsync</c> maps paths while the shared app is still pre-<c>StartAsync</c>.
/// </para>
/// </remarks>
public sealed class HostedCalendarCollectionApiEndpoint : IHostedService
{
    private readonly SharedHostedWebApp _sharedApp;
    private readonly ICalendarStore _calendarStore;
    private readonly IActiveTeamAccessor _activeTeam;
    private readonly NodeCallerSessionToken _callerAuth;
    private readonly ILogger<HostedCalendarCollectionApiEndpoint> _logger;

    /// <summary>Constructs the hosted calendar-collection API endpoint.</summary>
    public HostedCalendarCollectionApiEndpoint(
        SharedHostedWebApp sharedApp,
        ICalendarStore calendarStore,
        IActiveTeamAccessor activeTeam,
        NodeCallerSessionToken callerAuth,
        ILogger<HostedCalendarCollectionApiEndpoint> logger)
    {
        ArgumentNullException.ThrowIfNull(sharedApp);
        ArgumentNullException.ThrowIfNull(calendarStore);
        ArgumentNullException.ThrowIfNull(activeTeam);
        ArgumentNullException.ThrowIfNull(callerAuth);
        ArgumentNullException.ThrowIfNull(logger);

        _sharedApp = sharedApp;
        _calendarStore = calendarStore;
        _activeTeam = activeTeam;
        _callerAuth = callerAuth;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sharedApp.MapApiRoutes(app =>
            CalendarCollectionRoutes.Map(
                app.MapDeviceReachableProductDataGroup(),
                _calendarStore,
                _activeTeam,
                _callerAuth));

        _logger.LogInformation(
            "Node-local owned-calendar collection API registered over the recoverable local-node store " +
            "(GET/POST {Route}); per-route caller-auth {AuthState}.",
            CalendarCollectionRoutes.Route,
            _callerAuth.IsEnforced ? "ENFORCED" : "dev/un-enforced");

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
