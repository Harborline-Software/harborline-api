using Harborline.Api.Blocks.Calendar.Models;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.Calendar.Services;

/// <summary>
/// The <b>calendar-as-a-view</b> query (Slice S2): "a calendar is a VIEW = the events where X
/// participates" (schedule-feature design). The event is the single source of truth and appears on
/// every participant's calendar; this query is how you read one participant's (a Party's or an
/// Asset's) calendar — the per-doctor / per-patient / per-room view.
/// </summary>
/// <remarks>
/// <para>
/// <b>Tenant-scoped + cross-tenant isolated.</b> Every query takes a <see cref="TenantId"/> and only
/// ever sees that tenant's events (the store is composite-<c>(TenantId, Id)</c> keyed). A
/// participant ref that happens to collide across tenants never leaks one tenant's events into
/// another's view.
/// </para>
/// <para>
/// <b>Direction A (booking) only.</b> This is the read side of the booking model — it answers "what
/// is on this resource's / person's calendar in this window". It is <i>not</i> availability /
/// free-busy (that is the next slice: free/busy = availability windows − booked events) and it is
/// not the Direction-B utilization optimizer (a deferred overlay).
/// </para>
/// </remarks>
public interface ICalendarParticipantCalendarQuery
{
    /// <summary>
    /// The events on <paramref name="participant"/>'s calendar that overlap the date window
    /// [<paramref name="windowStart"/>, <paramref name="windowEnd"/>] — i.e. the events where this
    /// Party/Asset is a participant (in any role) AND which have at least one occurrence in the
    /// window. Tenant-scoped. A cancelled event/series contributes nothing.
    /// </summary>
    /// <param name="tenantId">The tenant whose calendar is queried; cross-tenant events are never returned.</param>
    /// <param name="participant">The Party or Asset whose calendar to read.</param>
    /// <param name="windowStart">Inclusive lower bound of the window.</param>
    /// <param name="windowEnd">Inclusive upper bound of the window.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The matching events in ascending <see cref="CalendarEvent.Start"/> order (distinct events, not occurrences).</returns>
    Task<IReadOnlyList<CalendarEvent>> EventsFor(
        TenantId tenantId,
        ParticipantRef participant,
        DateOnly windowStart,
        DateOnly windowEnd,
        CancellationToken ct = default);

    /// <summary>
    /// The concrete <see cref="EventOccurrence"/>s on <paramref name="participant"/>'s calendar in
    /// the window — each event from <see cref="EventsFor"/> expanded (EXDATE / RECURRENCE-ID applied)
    /// and flattened, in ascending start order. The agenda view of one participant's calendar.
    /// </summary>
    Task<IReadOnlyList<EventOccurrence>> OccurrencesFor(
        TenantId tenantId,
        ParticipantRef participant,
        DateOnly windowStart,
        DateOnly windowEnd,
        CancellationToken ct = default);
}
