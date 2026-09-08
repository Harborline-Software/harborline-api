namespace Harborline.Api.Blocks.Calendar.Models;

/// <summary>
/// A single concrete occurrence of a <see cref="CalendarEvent"/> within an expansion window —
/// the result of layering EXDATE (cancellations) and RECURRENCE-ID (overrides) over the raw
/// RRULE expansion. Occurrences are generated on demand, never pre-materialized
/// (capability-and-workflow-architecture.md §7.1).
/// </summary>
/// <param name="EventId">The series master (or single event) this occurrence belongs to.</param>
/// <param name="RecurrenceId">
/// The original occurrence date the RRULE generated — the stable RECURRENCE-ID key. For an
/// overridden occurrence, this is the <i>pre-override</i> date (so callers can correlate it back
/// to the series slot); for a normal occurrence it equals <see cref="Start"/>.
/// </param>
/// <param name="Start">The effective start date of this occurrence (after any override).</param>
/// <param name="End">The effective end date of this occurrence (inclusive, date-granular).</param>
/// <param name="Title">The effective title (the override's title, else the series title).</param>
/// <param name="IsOverride">True when this occurrence came from a RECURRENCE-ID override rather than the raw rule.</param>
public sealed record EventOccurrence(
    CalendarEventId EventId,
    DateOnly RecurrenceId,
    DateOnly Start,
    DateOnly End,
    string Title,
    bool IsOverride);
