namespace Harborline.Api.Blocks.Calendar.Models;

/// <summary>
/// Lifecycle status of a <see cref="CalendarEvent"/> (the series master or a
/// single non-recurring event). Mirrors RFC 5545 <c>STATUS</c> for VEVENT.
/// </summary>
/// <remarks>
/// This is the status of the <i>whole series/event</i>. A single cancelled
/// <i>occurrence</i> of a recurring series is NOT modeled here — that is an
/// EXDATE tombstone on the series (see <see cref="CalendarEvent.ExceptionDates"/>),
/// per capability-and-workflow-architecture.md §7.1.
/// </remarks>
public enum CalendarEventStatus
{
    /// <summary>The event is on the calendar and active (RFC 5545 CONFIRMED).</summary>
    Confirmed = 0,

    /// <summary>The event is tentative / not yet confirmed (RFC 5545 TENTATIVE).</summary>
    Tentative = 1,

    /// <summary>
    /// The whole event / series is cancelled (RFC 5545 CANCELLED). For a recurring
    /// series this cancels every occurrence; to cancel a <i>single</i> occurrence,
    /// add an EXDATE instead.
    /// </summary>
    Cancelled = 2,
}
