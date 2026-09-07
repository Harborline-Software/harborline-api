using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Harborline.Api.Blocks.Calendar.Models;
using Harborline.Api.Blocks.Calendar.Services;
using Harborline.Api.Foundation.Scheduling.DependencyInjection;

namespace Harborline.Api.Blocks.Calendar.DependencyInjection;

/// <summary>
/// DI registration for <c>blocks-calendar</c> (temporal core S0/S1 + participation model S2).
/// </summary>
public static class CalendarServiceCollectionExtensions
{
    /// <summary>
    /// Register the calendar block's services. Idempotent via <c>TryAdd</c>; pulls in
    /// <c>foundation-scheduling</c> (the shipped <c>IRruleExpansionService</c>) — no edits to that
    /// shared package.
    /// <list type="bullet">
    ///   <item><description><b>S0/S1</b> — <see cref="ICalendarEventExpansionService"/> (EXDATE +
    ///   RECURRENCE-ID + time-of-day/tz instants) and the in-memory series/occurrence store.</description></item>
    ///   <item><description><b>S2</b> — the participant calendar-view query
    ///   (<c>EventsFor</c> / <c>OccurrencesFor</c>).</description></item>
    ///   <item><description><b>S3</b> — the availability store + expansion (the bookable supply), the
    ///   free/busy service (<c>availability − ALL occupancy</c>), the by-context query
    ///   (<c>EventsForContext</c> — the coverage-support seam), and the Direction-A booking service
    ///   (book a free slot + no-double-book).</description></item>
    /// </list>
    /// </summary>
    public static IServiceCollection AddBlocksCalendar(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Reuse the shipped RRULE expander (rule-of-three: we consume it, we do not modify it).
        services.AddFoundationScheduling();

        // S0/S1 + S2
        services.TryAddSingleton<ICalendarEventExpansionService, CalendarEventExpansionService>();
        services.TryAddSingleton<ICalendarEventStore, InMemoryCalendarEventStore>();
        services.TryAddSingleton<ICalendarParticipantCalendarQuery, CalendarParticipantCalendarQuery>();

        // C1 (calendar productization #149) — the owned-calendar COLLECTION store (the demand-side
        // grouping an event belongs to; the entity that fills the CalendarId seam). In-memory default;
        // the node host overrides it with the durable NodeEfCalendarStore, exactly as for the event store.
        services.TryAddSingleton<ICalendarStore, InMemoryCalendarStore>();

        // S3 — availability + by-context + booking (Direction A)
        services.TryAddSingleton<IResourceAvailabilityStore, InMemoryResourceAvailabilityStore>();
        services.TryAddSingleton<IAvailabilityExpansionService, AvailabilityExpansionService>();
        services.TryAddSingleton<ICalendarContextQuery, CalendarContextQuery>();

        // Padding slice — the time-footprint policy (default = padding OFF / EventPadding.None). A
        // deployment opts in by registering a configured DefaultPaddingPolicy (or a richer Pack policy)
        // BEFORE calling this (TryAdd keeps the caller's registration). Free/busy + no-double-book reason
        // about the OCCUPIED interval ([start − Pre, end + Post]); the booking path seeds the default here.
        services.TryAddSingleton<IPaddingPolicy>(_ => new DefaultPaddingPolicy(EventPadding.None));

        // CALENDAR-LAYERS — shared (supply-side) calendars + subscriptions + the resolver, and the
        // pluggable detail-visibility policy (fail-closed owner-only default; a host registers a
        // grant-backed policy to widen visibility to authorized principals — see
        // IEventDetailVisibilityPolicy).
        services.TryAddSingleton<ISharedCalendarStore, InMemorySharedCalendarStore>();
        services.TryAddSingleton<ICalendarSubscriptionStore, InMemoryCalendarSubscriptionStore>();
        services.TryAddSingleton<ISharedCalendarResolver, SharedCalendarResolver>();
        services.TryAddSingleton<IEventDetailVisibilityPolicy, OwnerOnlyEventDetailVisibilityPolicy>();

        // BookingService + FreeBusyService are registered AFTER the resolver/visibility policy so their
        // layered constructors — which depend on the shared-calendar resolver registered just above — are
        // the ones the container activates (the booking gate then composes the SAME shared-exception layer
        // free/busy does, matching the view↔gate).
        services.TryAddSingleton<IBookingService, BookingService>();
        services.TryAddSingleton<IFreeBusyService, FreeBusyService>();
        return services;
    }
}
