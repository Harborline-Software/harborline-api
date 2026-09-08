using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Blocks.Calendar.Services;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.People;
using Harborline.Api.LocalNodeHost.Data.Scheduling;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>Maps the scheduling dogfood authoring API onto the shared listener.</summary>
public sealed class HostedSchedulingApiEndpoint(
    SharedHostedWebApp sharedApp, NodeSchedulingDraftStore store, SchedulingDraftValidator validator,
    NodeEfPartyRepository parties, IActiveTeamAccessor activeTeam,
    ICurrentUser currentUser, IBookingService bookingService, ICalendarEventStore eventStore,
    ICalendarStore calendarStore, IResourceAvailabilityStore availabilityStore,
    IFreeBusyService freeBusyService,
    TimeProvider timeProvider,
    ILogger<HostedSchedulingApiEndpoint> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        sharedApp.MapApiRoutes(app => SchedulingDefinitionRoutes.Map(
            app.MapDeviceReachableProductDataGroup(),
            store, validator, parties, activeTeam, currentUser, bookingService, eventStore,
            calendarStore, availabilityStore, freeBusyService, timeProvider));
        logger.LogInformation("Scheduling draft API registered at {RouteBase}.", SchedulingDefinitionRoutes.RouteBase);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
