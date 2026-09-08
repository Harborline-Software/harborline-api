namespace Harborline.Api.Blocks.Calendar.Models;

/// <summary>
/// A single recurring <b>bookable window</b> on a resource's availability (Slice S3) — "Mon–Fri
/// 9 a.m.–5 p.m.", "the worker is available Sat–Sun for shifts". The bookable <b>supply</b>: each
/// generated day of the window's recurrence is a span of wall-clock time, in the availability's
/// timezone, during which the resource can be booked.
/// </summary>
/// <remarks>
/// <para>
/// <b>A template, not concrete intervals.</b> Like a <see cref="CalendarEvent"/> series, an
/// availability window is a <i>definition</i> (an <see cref="Rrule"/> anchored at
/// <see cref="AnchorDate"/> with a wall-clock <see cref="StartTime"/>–<see cref="EndTime"/> on each
/// generated date); the concrete UTC intervals are produced on demand by the free/busy service using
/// the same shared RRULE expander + <c>TimezoneResolver</c> the event path uses (so a 9 a.m. window
/// keeps its 9 a.m. wall-clock across a DST boundary — DST applied consistently with the event
/// occurrences it is differenced against).
/// </para>
/// <para>
/// <b>Single-day windows only (by construction).</b> A window's <see cref="StartTime"/>–
/// <see cref="EndTime"/> is a within-day span (<see cref="EndTime"/> strictly after
/// <see cref="StartTime"/>); a resource available across midnight is two windows. This keeps each
/// generated date a clean <c>[date+start, date+end]</c> interval and avoids cross-day-span ambiguity.
/// A non-recurring (<see cref="Rrule"/> = null) window is a single one-day availability on
/// <see cref="AnchorDate"/>.
/// </para>
/// </remarks>
public sealed record AvailabilityWindow
{
    private AvailabilityWindow(
        DateOnly anchorDate,
        TimeOnly startTime,
        TimeOnly endTime,
        string? rrule)
    {
        AnchorDate = anchorDate;
        StartTime = startTime;
        EndTime = endTime;
        Rrule = rrule;
    }

    /// <summary>The recurrence anchor (DTSTART) — the first date the window applies / the recurrence walks from.</summary>
    public DateOnly AnchorDate { get; }

    /// <summary>The wall-clock start time-of-day of the bookable span (in the owning availability's timezone).</summary>
    public TimeOnly StartTime { get; }

    /// <summary>The wall-clock end time-of-day of the bookable span — strictly after <see cref="StartTime"/>.</summary>
    public TimeOnly EndTime { get; }

    /// <summary>
    /// RFC 5545 RRULE selecting which dates the window recurs on (e.g.
    /// <c>FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR</c> for "weekdays"); <see langword="null"/> for a single
    /// one-day window on <see cref="AnchorDate"/>.
    /// </summary>
    public string? Rrule { get; }

    /// <summary>True when this window recurs (carries an <see cref="Rrule"/>).</summary>
    public bool IsRecurring => !string.IsNullOrWhiteSpace(Rrule);

    /// <summary>
    /// Create an availability window. <paramref name="endTime"/> must be strictly after
    /// <paramref name="startTime"/> (a within-day span). An <paramref name="rrule"/>, when given,
    /// must be non-empty.
    /// </summary>
    /// <exception cref="ArgumentException">The time span is empty/inverted, or the rrule is empty.</exception>
    public static AvailabilityWindow Create(
        DateOnly anchorDate,
        TimeOnly startTime,
        TimeOnly endTime,
        string? rrule = null)
    {
        if (endTime <= startTime)
            throw new ArgumentException("Availability EndTime must be strictly after StartTime (a within-day span).", nameof(endTime));
        if (rrule is not null && string.IsNullOrWhiteSpace(rrule))
            throw new ArgumentException("Rrule, when provided, must be non-empty.", nameof(rrule));
        return new AvailabilityWindow(anchorDate, startTime, endTime, rrule);
    }
}
