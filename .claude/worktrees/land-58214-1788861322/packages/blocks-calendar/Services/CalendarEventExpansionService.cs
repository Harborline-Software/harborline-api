using Harborline.Api.Blocks.Calendar.Models;
using Harborline.Api.Foundation.Scheduling;

namespace Harborline.Api.Blocks.Calendar.Services;

/// <summary>
/// In-process <see cref="ICalendarEventExpansionService"/>. Composes the shipped
/// <see cref="IRruleExpansionService"/> and layers EXDATE + RECURRENCE-ID on top — the
/// occurrence-level semantics of capability-and-workflow-architecture.md §7.1 — without touching
/// the shared expander (rule-of-three: it has three live consumers).
/// </summary>
public sealed class CalendarEventExpansionService : ICalendarEventExpansionService
{
    private readonly IRruleExpansionService _rrule;

    public CalendarEventExpansionService(IRruleExpansionService rrule)
    {
        ArgumentNullException.ThrowIfNull(rrule);
        _rrule = rrule;
    }

    /// <inheritdoc />
    public IReadOnlyList<EventOccurrence> Expand(
        CalendarEvent calendarEvent,
        DateOnly windowStart,
        DateOnly windowEnd)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);
        if (windowEnd < windowStart)
            throw new ArgumentException("windowEnd must be on or after windowStart.", nameof(windowEnd));

        // A cancelled whole event/series yields nothing.
        if (calendarEvent.Status == CalendarEventStatus.Cancelled)
            return Array.Empty<EventOccurrence>();

        return calendarEvent.IsRecurring
            ? ExpandSeries(calendarEvent, windowStart, windowEnd)
            : ExpandSingle(calendarEvent, windowStart, windowEnd);
    }

    /// <inheritdoc />
    public IReadOnlyList<OccurrenceInstant> ExpandInstants(
        CalendarEvent calendarEvent,
        DateTimeOffset windowStartUtc,
        DateTimeOffset windowEndUtc)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);
        if (windowEndUtc < windowStartUtc)
            throw new ArgumentException("windowEndUtc must be on or after windowStartUtc.", nameof(windowEndUtc));

        if (calendarEvent.Status == CalendarEventStatus.Cancelled)
            return Array.Empty<OccurrenceInstant>();

        var tz = TimezoneResolver.Resolve(calendarEvent.Timezone);

        // The date-granular Expand filters by LOCAL date; the requested window is UTC. Widen the
        // local-date window by ±1 day so a local occurrence whose instant lands just inside the UTC
        // window (but whose local date sits a day off due to the tz offset) is not dropped at the
        // edges. We re-filter precisely against the UTC window after resolving instants.
        var localWindowStart = DateOnly.FromDateTime(windowStartUtc.UtcDateTime).AddDays(-1);
        var localWindowEnd = DateOnly.FromDateTime(windowEndUtc.UtcDateTime).AddDays(1);

        var dateOccurrences = Expand(calendarEvent, localWindowStart, localWindowEnd);

        var result = new List<OccurrenceInstant>(dateOccurrences.Count);
        foreach (var occ in dateOccurrences)
        {
            // Pick the time-of-day: an override may re-time the occurrence; otherwise inherit the
            // series master's wall-clock times. (Window membership already used the date-level
            // Expand; here we only attach the time-of-day and resolve to UTC.)
            var (startTime, endTime) = ResolveOccurrenceTimes(calendarEvent, occ);

            var startUtc = TimezoneResolver.ToUtcInstant(occ.Start, startTime, tz);
            var endUtc = TimezoneResolver.ToUtcInstant(occ.End, endTime, tz);

            // Precise UTC-window filter (inclusive bounds). Keep an instant that overlaps the window.
            if (endUtc < windowStartUtc || startUtc > windowEndUtc)
                continue;

            result.Add(new OccurrenceInstant(
                EventId:      occ.EventId,
                RecurrenceId: occ.RecurrenceId,
                StartUtc:     startUtc,
                EndUtc:       endUtc,
                Title:        occ.Title,
                IsOverride:   occ.IsOverride));
        }

        result.Sort(static (a, b) =>
        {
            var byStart = a.StartUtc.CompareTo(b.StartUtc);
            return byStart != 0 ? byStart : a.RecurrenceId.CompareTo(b.RecurrenceId);
        });
        return result;
    }

    /// <summary>
    /// The wall-clock (start, end) time-of-day for an occurrence: an override's times when it set
    /// them, else the series master's times. Falls back to the master for a null override-time so
    /// re-timing only the start (or only the end) inherits the other from the master.
    /// </summary>
    private static (TimeOnly Start, TimeOnly End) ResolveOccurrenceTimes(CalendarEvent ev, EventOccurrence occ)
    {
        if (occ.IsOverride && ev.Overrides.TryGetValue(occ.RecurrenceId, out var ov))
        {
            return (ov.NewStartTime ?? ev.StartTime, ov.NewEndTime ?? ev.EndTime);
        }
        return (ev.StartTime, ev.EndTime);
    }

    // ----------------------------------------------------------------
    // Single (non-recurring) event
    // ----------------------------------------------------------------

    private static IReadOnlyList<EventOccurrence> ExpandSingle(
        CalendarEvent ev, DateOnly windowStart, DateOnly windowEnd)
    {
        // The event intersects the window when its [Start, End] range overlaps [windowStart, windowEnd].
        if (ev.End < windowStart || ev.Start > windowEnd)
            return Array.Empty<EventOccurrence>();

        return new[]
        {
            new EventOccurrence(
                EventId:      ev.Id,
                RecurrenceId: ev.Start,
                Start:        ev.Start,
                End:          ev.End,
                Title:        ev.Title,
                IsOverride:   false),
        };
    }

    // ----------------------------------------------------------------
    // Recurring series — raw RRULE − EXDATE + RECURRENCE-ID overrides
    // ----------------------------------------------------------------

    private IReadOnlyList<EventOccurrence> ExpandSeries(
        CalendarEvent ev, DateOnly windowStart, DateOnly windowEnd)
    {
        var durationDays = ev.End.DayNumber - ev.Start.DayNumber; // each occurrence keeps the master's span

        // 1. Raw RRULE occurrences for the requested window. We expand from the series anchor
        //    (ev.Start) and bound the horizon at windowEnd; leadDays=0 so nothing earlier than
        //    `today` is dropped — we set `today = ev.Start` so the lead filter never trims the
        //    front of the window (we filter the window ourselves below). lookaheadDays is unused
        //    because we pass an explicit `end`.
        var raw = _rrule.ExpandOccurrences(
            rrule:          ev.Rrule!,
            start:          ev.Start,
            end:            windowEnd,
            lookaheadDays:  0,
            leadDays:       0,
            today:          ev.Start,
            timezone:       ev.Timezone);

        var exDates = ev.ExceptionDates;          // EXDATE set (HashSet/SortedSet — O(1)/O(log n) lookup)
        var overrides = ev.Overrides;             // RECURRENCE-ID → override

        var result = new List<EventOccurrence>();

        // 2. Walk the raw occurrences: drop EXDATE'd; substitute overrides; window-filter.
        foreach (var occDate in raw)
        {
            // EXDATE cancels this occurrence outright.
            if (exDates.Contains(occDate))
                continue;

            if (overrides.TryGetValue(occDate, out var ov))
            {
                // RECURRENCE-ID override. A cancelled override is omitted; otherwise the override
                // values replace the generated occurrence. Window membership is judged by the
                // override's NEW start (it may have moved into/out of the window).
                if (ov.IsCancelled)
                    continue;

                if (InWindow(ov.NewStart, ov.NewEnd ?? ov.NewStart, windowStart, windowEnd))
                {
                    result.Add(new EventOccurrence(
                        EventId:      ev.Id,
                        RecurrenceId: occDate,
                        Start:        ov.NewStart,
                        End:          ov.NewEnd ?? ov.NewStart,
                        Title:        ov.NewTitle ?? ev.Title,
                        IsOverride:   true));
                }
                continue;
            }

            // Normal generated occurrence — keep the master's title + span. Window-filter on the
            // [occStart, occEnd] range so a multi-day occurrence straddling windowStart is kept.
            var occEnd = occDate.AddDays(durationDays);
            if (InWindow(occDate, occEnd, windowStart, windowEnd))
            {
                result.Add(new EventOccurrence(
                    EventId:      ev.Id,
                    RecurrenceId: occDate,
                    Start:        occDate,
                    End:          occEnd,
                    Title:        ev.Title,
                    IsOverride:   false));
            }
        }

        // 3. An override may move an occurrence whose ORIGINAL RECURRENCE-ID fell OUTSIDE the
        //    raw-expansion window (windowEnd bounded the raw set) but whose NEW start lands inside
        //    the window. Catch those by scanning overrides whose RecurrenceId is beyond windowEnd
        //    yet whose NewStart is within the window. (Overrides whose RecurrenceId <= windowEnd
        //    were already handled in the loop above.)
        foreach (var (recurrenceId, ov) in overrides)
        {
            if (ov.IsCancelled) continue;
            if (recurrenceId <= windowEnd) continue;                  // already considered above
            if (exDates.Contains(recurrenceId)) continue;             // EXDATE wins over a stale override
            if (!InWindow(ov.NewStart, ov.NewEnd ?? ov.NewStart, windowStart, windowEnd)) continue;

            result.Add(new EventOccurrence(
                EventId:      ev.Id,
                RecurrenceId: recurrenceId,
                Start:        ov.NewStart,
                End:          ov.NewEnd ?? ov.NewStart,
                Title:        ov.NewTitle ?? ev.Title,
                IsOverride:   true));
        }

        result.Sort(static (a, b) =>
        {
            var byStart = a.Start.CompareTo(b.Start);
            return byStart != 0 ? byStart : a.RecurrenceId.CompareTo(b.RecurrenceId);
        });
        return result;
    }

    private static bool InWindow(DateOnly start, DateOnly end, DateOnly windowStart, DateOnly windowEnd)
        => end >= windowStart && start <= windowEnd;
}
