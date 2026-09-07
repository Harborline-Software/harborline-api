namespace Harborline.Api.Blocks.Calendar.Models;

/// <summary>
/// A single modified occurrence of a recurring <see cref="CalendarEvent"/> —
/// the RFC 5545 <c>RECURRENCE-ID</c> override (the "edit this occurrence" result).
/// </summary>
/// <remarks>
/// <para>
/// Per capability-and-workflow-architecture.md §7.1: editing a single occurrence of a
/// series produces a <i>detached occurrence</i> keyed by its
/// <see cref="RecurrenceId"/> (the original, unmodified occurrence date the recurrence
/// rule would have generated). The override does NOT alter the series master; expansion
/// replaces the generated occurrence at <see cref="RecurrenceId"/> with this override's
/// values.
/// </para>
/// <para>
/// <b>Date-granular (Slice S0).</b> <see cref="RecurrenceId"/> and <see cref="NewStart"/>/
/// <see cref="NewEnd"/> are <see cref="DateOnly"/>; the override can move an occurrence to a
/// different date and/or retitle it. Time-of-day overrides are Slice S1.
/// </para>
/// <para>
/// An override may also be a <i>cancellation-as-edit</i> via <see cref="IsCancelled"/>, but
/// the canonical way to cancel a single occurrence is an EXDATE on the series
/// (<see cref="CalendarEvent.ExceptionDates"/>); <see cref="IsCancelled"/> exists so a
/// previously-edited (detached) occurrence can be cancelled without losing the override
/// record — the §7.1 "preserve, never silently clobber" discipline.
/// </para>
/// </remarks>
public sealed record OccurrenceOverride
{
    /// <summary>
    /// The RECURRENCE-ID: the original occurrence date (as the series rule would have
    /// generated it) that this override replaces. This is the stable key — it does NOT
    /// change even when the override moves the occurrence to <see cref="NewStart"/>.
    /// </summary>
    public required DateOnly RecurrenceId { get; init; }

    /// <summary>The overridden start date for this occurrence (may differ from <see cref="RecurrenceId"/>).</summary>
    public required DateOnly NewStart { get; init; }

    /// <summary>The overridden end date for this occurrence (inclusive, date-granular). <see langword="null"/> ⇒ same as <see cref="NewStart"/>.</summary>
    public DateOnly? NewEnd { get; init; }

    /// <summary>
    /// The overridden wall-clock start time-of-day (Slice S1), interpreted in the series'
    /// <c>Timezone</c>. <see langword="null"/> ⇒ inherit the series master's <c>StartTime</c>. Lets
    /// a single occurrence be re-timed (a one-off 8 a.m. instead of the usual 9 a.m.) without
    /// touching the series.
    /// </summary>
    public TimeOnly? NewStartTime { get; init; }

    /// <summary>The overridden wall-clock end time-of-day (Slice S1). <see langword="null"/> ⇒ inherit the series master's <c>EndTime</c>.</summary>
    public TimeOnly? NewEndTime { get; init; }

    /// <summary>The overridden title for this occurrence. <see langword="null"/> ⇒ inherit the series title.</summary>
    public string? NewTitle { get; init; }

    /// <summary>
    /// True when this detached occurrence is cancelled (it disappears from the expansion,
    /// but the override record is preserved so a later series edit does not silently
    /// resurrect or clobber it). Prefer an EXDATE for a plain cancellation; this flag is for
    /// cancelling an occurrence that was <i>already</i> edited.
    /// </summary>
    public bool IsCancelled { get; init; }
}
