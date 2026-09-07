using Harborline.Api.Blocks.Calendar.Models;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.Calendar.Services;

/// <summary>
/// The <b>by-context</b> query (Slice S3) — "what is scheduled against this context" (a position /
/// floor / project / case). The symmetric twin of the S2 by-participant
/// <see cref="ICalendarParticipantCalendarQuery.EventsFor"/>: where that reads a resource's calendar,
/// this reads a context's schedule. Together they make the calendar core <b>queryable by participant
/// AND by context</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>THE coverage-support condition.</b> This is the data a future coverage / rostering overlay
/// (Pattern C) needs and the calendar core does <i>not</i> have to understand to provide: the overlay
/// asks "what events are scheduled against ICU-floor-3 this shift", reads back the events (with their
/// <see cref="CalendarParticipation"/> set + <see cref="Occupancy"/> + occurrence times), and computes
/// coverage = present-qualified-participants vs. its own minimum — <b>entirely outside the calendar</b>.
/// The calendar computes nothing about coverage; it only matches events by their opaque
/// <see cref="CalendarEvent.ScheduledAgainst"/> ref. That is how the intent-agnostic core SUPPORTS
/// coverage without knowing about coverage.
/// </para>
/// <para><b>Tenant-scoped + cross-tenant isolated</b>, like every other calendar read.</para>
/// </remarks>
public interface ICalendarContextQuery
{
    /// <summary>
    /// The events scheduled against <paramref name="context"/> that have at least one occurrence in
    /// the date window — i.e. the events whose <see cref="CalendarEvent.ScheduledAgainst"/> equals
    /// <paramref name="context"/> (an exact opaque-ref match) AND which land in the window.
    /// Tenant-scoped; a context ref that collides across tenants never leaks events. A cancelled
    /// event/series contributes nothing.
    /// </summary>
    /// <param name="tenantId">The tenant whose schedule is queried; cross-tenant events are never returned.</param>
    /// <param name="context">The opaque context the events are scheduled against.</param>
    /// <param name="windowStart">Inclusive lower bound of the window.</param>
    /// <param name="windowEnd">Inclusive upper bound of the window.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The matching events in ascending <see cref="CalendarEvent.Start"/> order (distinct events, not occurrences).</returns>
    Task<IReadOnlyList<CalendarEvent>> EventsForContext(
        TenantId tenantId,
        ContextRef context,
        DateOnly windowStart,
        DateOnly windowEnd,
        CancellationToken ct = default);
}
