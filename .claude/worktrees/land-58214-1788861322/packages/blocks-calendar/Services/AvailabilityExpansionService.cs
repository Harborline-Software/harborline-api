using Harborline.Api.Blocks.Calendar.Models;
using Harborline.Api.Foundation.Scheduling;

namespace Harborline.Api.Blocks.Calendar.Services;

/// <summary>
/// In-process <see cref="IAvailabilityExpansionService"/> (Slice S3). Composes the shipped
/// <see cref="IRruleExpansionService"/> (read-only — rule-of-three: it has live consumers and is not
/// modified) and resolves each generated window-date to a UTC interval via
/// <see cref="TimezoneResolver"/> — the same DST-aware path the event occurrences use, so the supply
/// and the occupancy it is differenced against share a timezone treatment.
/// </summary>
public sealed class AvailabilityExpansionService : IAvailabilityExpansionService
{
    private readonly IRruleExpansionService _rrule;

    public AvailabilityExpansionService(IRruleExpansionService rrule)
    {
        ArgumentNullException.ThrowIfNull(rrule);
        _rrule = rrule;
    }

    /// <inheritdoc />
    public IReadOnlyList<TimeInterval> Expand(
        ResourceAvailability availability,
        DateTimeOffset windowStartUtc,
        DateTimeOffset windowEndUtc)
        => ExpandCore(availability, windowStartUtc, windowEndUtc, additionalExceptionDays: null);

    /// <inheritdoc />
    public IReadOnlyList<TimeInterval> Expand(
        ResourceAvailability availability,
        DateTimeOffset windowStartUtc,
        DateTimeOffset windowEndUtc,
        IReadOnlySet<DateOnly> additionalExceptionDays)
    {
        ArgumentNullException.ThrowIfNull(additionalExceptionDays);
        return ExpandCore(availability, windowStartUtc, windowEndUtc, additionalExceptionDays);
    }

    private IReadOnlyList<TimeInterval> ExpandCore(
        ResourceAvailability availability,
        DateTimeOffset windowStartUtc,
        DateTimeOffset windowEndUtc,
        IReadOnlySet<DateOnly>? additionalExceptionDays)
    {
        ArgumentNullException.ThrowIfNull(availability);
        if (windowEndUtc < windowStartUtc)
            throw new ArgumentException("windowEndUtc must be on or after windowStartUtc.", nameof(windowEndUtc));

        if (availability.Windows.Count == 0)
            return Array.Empty<TimeInterval>();

        var tz = TimezoneResolver.Resolve(availability.Timezone);

        // The window dates are computed in the resource's local tz. Widen the local-date span by ±1
        // day so a window whose local date sits a day off the UTC bound (due to the offset) is not
        // dropped — the precise UTC clip below re-trims. (Mirrors CalendarEventExpansionService.)
        var localWindowStart = DateOnly.FromDateTime(windowStartUtc.UtcDateTime).AddDays(-1);
        var localWindowEnd = DateOnly.FromDateTime(windowEndUtc.UtcDateTime).AddDays(1);

        var intervals = new List<TimeInterval>();

        foreach (var window in availability.Windows)
        {
            IEnumerable<DateOnly> dates = window.IsRecurring
                ? _rrule.ExpandOccurrences(
                    rrule:         window.Rrule!,
                    start:         window.AnchorDate,
                    end:           localWindowEnd,
                    lookaheadDays: 0,
                    leadDays:      0,
                    today:         window.AnchorDate,
                    timezone:      availability.Timezone)
                // A single (non-recurring) window applies only on its anchor date.
                : (window.AnchorDate >= localWindowStart && window.AnchorDate <= localWindowEnd
                    ? new[] { window.AnchorDate }
                    : Array.Empty<DateOnly>());

            foreach (var date in dates)
            {
                if (date < localWindowStart || date > localWindowEnd) continue;
                // Whole-day removal — the resource's OWN supply-side exceptions: a single-day holiday
                // (ExceptionDates) OR a day inside a spanning vacation/closure (ExceptionSpans, Slice
                // CALENDAR-LAYERS) — UNIONED with any shared-calendar holiday/closure days the caller
                // resolved (additionalExceptionDays, the supply-side layer composition). All are
                // whole-day removals applied at the same local-date check, BEFORE the wall-clock→UTC
                // resolution — so the UTC invariant is untouched (surviving intervals are still
                // differenced as absolute UTC instants downstream).
                if (availability.IsExcepted(date)) continue;
                if (additionalExceptionDays is not null && additionalExceptionDays.Contains(date)) continue;

                var startUtc = TimezoneResolver.ToUtcInstant(date, window.StartTime, tz);
                var endUtc = TimezoneResolver.ToUtcInstant(date, window.EndTime, tz);
                if (endUtc <= startUtc) continue;          // defensive (Create already guards within-day span)

                if (IntervalMath.Clip(new TimeInterval(startUtc, endUtc), windowStartUtc, windowEndUtc) is { } clipped)
                    intervals.Add(clipped);
            }
        }

        // Merge overlapping/adjacent windows (e.g. a "weekdays 9-12" + "weekdays 13-17" pair stays two
        // intervals; an accidental "9-17" + "10-12" pair coalesces to one).
        return IntervalMath.Merge(intervals);
    }
}
