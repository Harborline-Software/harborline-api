namespace Harborline.Api.Blocks.Calendar.Models;

/// <summary>
/// A <b>supply-side availability exception over a whole-day DATE SPAN</b> — the calendar-layers
/// generalization of S3's single-day <see cref="ResourceAvailability.ExceptionDates"/> (Slice
/// CALENDAR-LAYERS). A vacation ("the doctor is off Mon 03-09 through Fri 03-13"), a clinic closure,
/// a holiday block: every availability window on every day in <c>[Start, End]</c> (inclusive of both
/// endpoints, whole-day granularity) is suppressed when the supply is expanded.
/// </summary>
/// <remarks>
/// <para>
/// <b>Whole-day, inclusive both ends.</b> Like an EXDATE, an exception span is date-granular — it
/// removes <i>entire days</i> of availability, not a partial-day window. A partial-day removal (a
/// lunch, an afternoon-only block) is modeled instead as an <see cref="Occupancy.Blocking"/> event,
/// which free/busy subtracts as occupancy. The span is INCLUSIVE of both <see cref="Start"/> and
/// <see cref="End"/> (a one-day vacation is <c>Start == End</c>), matching the way a user reads "off
/// from the 9th through the 13th".
/// </para>
/// <para>
/// <b>Two supply-side homes for the same mechanism.</b> A <see cref="ResourceAvailability"/> carries
/// per-resource exception spans (this resource's own vacation / personal closure); a
/// <see cref="SharedCalendar"/> carries org/location-wide exception spans (a clinic holiday that
/// applies to every subscribed resource). Both expand the same way (the day is suppressed); the
/// difference is scope (one resource vs. a subscribed set). The free/busy composition unions both
/// before differencing against availability.
/// </para>
/// </remarks>
public sealed record ExceptionSpan
{
    private ExceptionSpan(DateOnly start, DateOnly end, string? reason)
    {
        Start = start;
        End = end;
        Reason = reason;
    }

    /// <summary>The first whole day the resource is unavailable (inclusive).</summary>
    public DateOnly Start { get; }

    /// <summary>The last whole day the resource is unavailable (inclusive). On or after <see cref="Start"/>.</summary>
    public DateOnly End { get; }

    /// <summary>
    /// An optional human-readable reason ("Vacation", "Thanksgiving", "Clinic closed for renovation").
    /// Carried for display only — the calendar core does not reason about it (per the litmus, a
    /// reason is not a property free/busy reasons about, so it stays a thin descriptive field, not a
    /// vertical-coupled concept).
    /// </summary>
    public string? Reason { get; }

    /// <summary>True when <paramref name="date"/> falls within this span (inclusive of both ends).</summary>
    public bool Contains(DateOnly date) => date >= Start && date <= End;

    /// <summary>
    /// Create an exception span over <c>[<paramref name="start"/>, <paramref name="end"/>]</c>
    /// (inclusive). <paramref name="end"/> must be on or after <paramref name="start"/>; a single-day
    /// span uses <c>start == end</c>.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="end"/> is before <paramref name="start"/>.</exception>
    public static ExceptionSpan Create(DateOnly start, DateOnly end, string? reason = null)
    {
        if (end < start)
            throw new ArgumentException("Exception-span End must be on or after Start (inclusive whole-day span).", nameof(end));
        return new ExceptionSpan(start, end, reason);
    }

    /// <summary>Enumerate every whole day in the span (inclusive both ends).</summary>
    public IEnumerable<DateOnly> Days()
    {
        for (var d = Start; d <= End; d = d.AddDays(1))
            yield return d;
    }

    public override string ToString()
        => Reason is null ? $"{Start:O}..{End:O}" : $"{Start:O}..{End:O} ({Reason})";
}
