using Harborline.Api.Blocks.Calendar.Models;

namespace Harborline.Api.Blocks.Calendar.Services;

/// <summary>
/// Expand a <see cref="CalendarEvent"/> (single event or recurring series) into its concrete
/// occurrences within a date window, layering the occurrence-level semantics the shared RRULE
/// expander does not have: <b>EXDATE</b> cancellations and <b>RECURRENCE-ID</b> overrides
/// (capability-and-workflow-architecture.md §7.1).
/// </summary>
/// <remarks>
/// <para>
/// This service is the local override layer the schedule-feature survey calls for: it composes
/// <c>Harborline.Api.Foundation.Scheduling.IRruleExpansionService</c> (the shipped RFC 5545 expander —
/// FREQ/INTERVAL/COUNT/UNTIL/BYDAY/BYMONTHDAY/BYMONTH) and adds EXDATE + RECURRENCE-ID handling
/// <i>in this block</i>. The shared expander has three live consumers and is NOT modified
/// (rule-of-three); the override layer is promoted to <c>foundation-scheduling</c> only if a 2nd
/// consumer needs it.
/// </para>
/// <para><b>Slice S0 is date-granular</b> (<see cref="DateOnly"/>); time-of-day is Slice S1.</para>
/// </remarks>
public interface ICalendarEventExpansionService
{
    /// <summary>
    /// Expand <paramref name="calendarEvent"/> into its occurrences within
    /// [<paramref name="windowStart"/>, <paramref name="windowEnd"/>], applying EXDATE
    /// cancellations and RECURRENCE-ID overrides.
    /// </summary>
    /// <param name="calendarEvent">The single event or recurring series master to expand.</param>
    /// <param name="windowStart">Inclusive lower bound of the requested window.</param>
    /// <param name="windowEnd">Inclusive upper bound of the requested window.</param>
    /// <returns>
    /// Occurrences in ascending <see cref="EventOccurrence.Start"/> order. A non-recurring event
    /// yields at most one occurrence (when it intersects the window). For a series:
    /// raw RRULE occurrences minus EXDATE dates, with RECURRENCE-ID overrides substituted; a
    /// cancelled override (<see cref="OccurrenceOverride.IsCancelled"/>) is omitted; an override
    /// that moves an occurrence to a date inside the window is included even if its original
    /// RECURRENCE-ID fell outside it (and vice-versa).
    /// </returns>
    IReadOnlyList<EventOccurrence> Expand(
        CalendarEvent calendarEvent,
        DateOnly windowStart,
        DateOnly windowEnd);

    /// <summary>
    /// Expand <paramref name="calendarEvent"/> into <b>UTC instants</b> (Slice S1 — time-of-day +
    /// timezone/DST) within [<paramref name="windowStartUtc"/>, <paramref name="windowEndUtc"/>].
    /// Each occurrence's wall-clock <c>StartTime</c>/<c>EndTime</c> (or the override's times) is
    /// interpreted in the event's IANA <c>Timezone</c> on the occurrence date and converted to UTC
    /// with DST applied — so a recurring 9 a.m. event keeps its 9 a.m. wall-clock across a
    /// spring-forward / fall-back boundary while its UTC instant shifts by an hour.
    /// </summary>
    /// <param name="calendarEvent">The single event or recurring series master to expand.</param>
    /// <param name="windowStartUtc">Inclusive lower UTC bound of the requested window.</param>
    /// <param name="windowEndUtc">Inclusive upper UTC bound of the requested window.</param>
    /// <returns>
    /// Occurrences as UTC <see cref="OccurrenceInstant"/>s in ascending start order, with the same
    /// EXDATE / RECURRENCE-ID semantics as <see cref="Expand"/> applied first (at the date level),
    /// then each surviving occurrence resolved to its instant and filtered to the UTC window.
    /// </returns>
    IReadOnlyList<OccurrenceInstant> ExpandInstants(
        CalendarEvent calendarEvent,
        DateTimeOffset windowStartUtc,
        DateTimeOffset windowEndUtc);
}
