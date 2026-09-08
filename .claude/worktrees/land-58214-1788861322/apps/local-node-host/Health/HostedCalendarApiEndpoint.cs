using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.Blocks.Calendar.Services;
using Harborline.Api.Kernel.Runtime.Teams;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Hosted service that maps the node-local READ-ONLY calendar routes (agenda + free/busy) onto the
/// shared Kestrel listener (ONR app-calendar survey 2026-06-24, the thin first slice).
/// </summary>
/// <remarks>
/// <para>
/// A thin <see cref="IHostedService"/> wrapper: the route mapping itself lives in
/// <see cref="CalendarRoutes.Map"/> (the single source of truth shared with the route tests so the
/// wire contract has no test/prod drift). The calendar query / expansion / free-busy services are
/// resolved from the OUTER host container (the block's services, with the durable <c>NodeEf</c> stores
/// behind the interfaces) and passed to <see cref="CalendarRoutes.Map"/> as closed-over dependencies.
/// Resolving via <c>[FromServices]</c> inside the route handlers would fail because the routes are
/// mapped onto <see cref="SharedHostedWebApp"/>'s inner <c>WebApplication</c>, whose service provider
/// does NOT have the outer-container registrations (bug-2849).
/// </para>
/// <para>
/// Registration order: added to the composition root BEFORE <see cref="SharedHostedWebApp"/> so its
/// <c>StartAsync</c> maps paths while the shared app is still pre-<c>StartAsync</c>.
/// </para>
/// </remarks>
public sealed class HostedCalendarApiEndpoint : IHostedService
{
    private readonly SharedHostedWebApp _sharedApp;
    private readonly ICalendarParticipantCalendarQuery _participantQuery;
    private readonly ICalendarEventStore _eventStore;
    private readonly ICalendarEventExpansionService _expansion;
    private readonly IFreeBusyService _freeBusy;
    private readonly IActiveTeamAccessor _activeTeam;
    private readonly ILogger<HostedCalendarApiEndpoint> _logger;

    /// <summary>Constructs the hosted read-only calendar API endpoint.</summary>
    public HostedCalendarApiEndpoint(
        SharedHostedWebApp sharedApp,
        ICalendarParticipantCalendarQuery participantQuery,
        ICalendarEventStore eventStore,
        ICalendarEventExpansionService expansion,
        IFreeBusyService freeBusy,
        IActiveTeamAccessor activeTeam,
        ILogger<HostedCalendarApiEndpoint> logger)
    {
        ArgumentNullException.ThrowIfNull(sharedApp);
        ArgumentNullException.ThrowIfNull(participantQuery);
        ArgumentNullException.ThrowIfNull(eventStore);
        ArgumentNullException.ThrowIfNull(expansion);
        ArgumentNullException.ThrowIfNull(freeBusy);
        ArgumentNullException.ThrowIfNull(activeTeam);
        ArgumentNullException.ThrowIfNull(logger);

        _sharedApp = sharedApp;
        _participantQuery = participantQuery;
        _eventStore = eventStore;
        _expansion = expansion;
        _freeBusy = freeBusy;
        _activeTeam = activeTeam;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sharedApp.MapApiRoutes(app =>
            CalendarRoutes.Map(
                app.MapDeviceReachableProductDataGroup(),
                _participantQuery,
                _eventStore,
                _expansion,
                _freeBusy,
                _activeTeam));

        _logger.LogInformation(
            "Node-local READ-ONLY calendar API registered over the recoverable local-node store " +
            "(GET {RouteBase}/occurrences, GET {RouteBase}/free-busy).",
            CalendarRoutes.RouteBase,
            CalendarRoutes.RouteBase);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
