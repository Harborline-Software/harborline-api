namespace Harborline.Api.Blocks.Calendar.Models;

/// <summary>
/// A single concrete occurrence resolved to <b>UTC instants</b> — the Slice S1 time-of-day +
/// timezone/DST projection of an <see cref="EventOccurrence"/>. Where <see cref="EventOccurrence"/>
/// is date-granular (Slice S0), an <see cref="OccurrenceInstant"/> carries real
/// <see cref="DateTimeOffset"/> start/end so a 30-minute appointment slot is a true instant pair,
/// not just a date. This matches the <c>blocks-scheduling</c> <c>SlotReservation</c>'s
/// <see cref="DateTimeOffset"/> (UTC) convention (the reservation path is already timed; S1 makes
/// the recurrence path timed too).
/// </summary>
/// <param name="EventId">The series master (or single event) this occurrence belongs to.</param>
/// <param name="RecurrenceId">
/// The original occurrence <i>date</i> the RRULE generated — the stable RECURRENCE-ID key (the
/// pre-override date for an overridden occurrence; equals <see cref="StartUtc"/>'s local date for a
/// normal one). Kept date-granular because RECURRENCE-ID identifies the series slot, which the rule
/// still generates by date.
/// </param>
/// <param name="StartUtc">
/// The effective start instant in UTC — the occurrence's wall-clock start time-of-day, interpreted
/// in the event's IANA timezone on the occurrence date, converted to UTC with DST applied.
/// </param>
/// <param name="EndUtc">The effective end instant in UTC (after any override).</param>
/// <param name="Title">The effective title (the override's title, else the series title).</param>
/// <param name="IsOverride">True when this occurrence came from a RECURRENCE-ID override rather than the raw rule.</param>
/// <remarks>
/// <para>
/// <b>Why UTC.</b> The instant is the durable, comparable, storage-and-coordination form; the event's
/// <c>Timezone</c> + the original wall-clock <c>StartTime</c>/<c>EndTime</c> on the master remain the
/// source of truth for display. A recurring 9 a.m. event keeps its 9 a.m. wall-clock across a DST
/// boundary; its UTC instant shifts by an hour — which is exactly the behavior callers need for
/// conflict-detection / reservation against the already-UTC <c>SlotReservation</c> path.
/// </para>
/// </remarks>
public sealed record OccurrenceInstant(
    CalendarEventId EventId,
    DateOnly RecurrenceId,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string Title,
    bool IsOverride)
{
    /// <summary>
    /// The <b>visible / booked</b> interval — exactly <c>[StartUtc, EndUtc)</c>. This is what the
    /// demand side sees and books (a 2:00–2:30 appointment). The padding slice distinguishes this from
    /// the resource-occupied interval (<see cref="OccupiedInterval"/>).
    /// </summary>
    public TimeInterval VisibleInterval => new(StartUtc, EndUtc);

    /// <summary>
    /// The <b>occupied</b> interval — the visible interval extended by the event's padding envelope:
    /// <c>[StartUtc − padding.Pre, EndUtc + padding.Post)</c>. This is what blocks the resource for
    /// free/busy + no-double-book (a 2:00–2:30 appt with 5-min pre + 30-min post occupies 1:55–3:00).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Load-bearing UTC invariant (S3 deep-review).</b> Padding is applied to the <i>absolute UTC
    /// instants</i> here — the start/end were already resolved by differencing UTC instants via the
    /// shared <c>TimezoneResolver</c>, so this is pure UTC arithmetic. Padding is <b>never</b> added in
    /// wall-clock. That is what keeps a padded recurrence correct across a DST boundary: the visible
    /// instants already shifted by the DST delta, and a flat UTC duration added on top preserves the
    /// real elapsed footprint (a 30-min post-padding is 30 real minutes regardless of any wall-clock
    /// discontinuity in the span).
    /// </para>
    /// <para>
    /// With <see cref="EventPadding.None"/> (the backward-compatible default) the occupied interval
    /// equals the visible interval — an unpadded event blocks exactly its visible span, as before this
    /// slice.
    /// </para>
    /// </remarks>
    /// <param name="padding">The event's padding envelope (resolved at booking / overridden per event).</param>
    /// <returns>The occupied UTC interval — <see cref="VisibleInterval"/> when <paramref name="padding"/> is <see cref="EventPadding.None"/>.</returns>
    public TimeInterval OccupiedInterval(EventPadding padding)
        => padding.IsNone
            ? new TimeInterval(StartUtc, EndUtc)
            : new TimeInterval(StartUtc - padding.Pre, EndUtc + padding.Post);
}
