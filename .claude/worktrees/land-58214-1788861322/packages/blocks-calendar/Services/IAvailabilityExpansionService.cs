using Harborline.Api.Blocks.Calendar.Models;

namespace Harborline.Api.Blocks.Calendar.Services;

/// <summary>
/// Expand a <see cref="ResourceAvailability"/> (its recurring <see cref="AvailabilityWindow"/>s, minus
/// whole-day <see cref="ResourceAvailability.ExceptionDates"/>) into concrete <b>UTC intervals</b>
/// within a window (Slice S3). The bookable supply, resolved to real instants — the first operand of
/// <c>free = availability − occupancy</c>.
/// </summary>
/// <remarks>
/// Each window is a recurrence template; this service walks the shared RRULE expander over the
/// window's rule (read-only, the same expander the event path uses) and resolves each generated
/// date's wall-clock <see cref="AvailabilityWindow.StartTime"/>–<see cref="AvailabilityWindow.EndTime"/>
/// to UTC via <c>TimezoneResolver</c> (DST applied — a 9 a.m. window keeps its 9 a.m. wall-clock
/// across a DST boundary, consistent with the event occurrences it is differenced against). Generated
/// intervals on an exception date are dropped; overlapping intervals from multiple windows are merged;
/// the result is clipped to the requested UTC window and ordered by start.
/// </remarks>
public interface IAvailabilityExpansionService
{
    /// <summary>
    /// The bookable UTC intervals for <paramref name="availability"/> within
    /// [<paramref name="windowStartUtc"/>, <paramref name="windowEndUtc"/>], merged and ordered by
    /// start. Empty when the resource has no window touching the requested window.
    /// </summary>
    IReadOnlyList<TimeInterval> Expand(
        ResourceAvailability availability,
        DateTimeOffset windowStartUtc,
        DateTimeOffset windowEndUtc);

    /// <summary>
    /// The bookable UTC intervals (Slice CALENDAR-LAYERS overload), additionally suppressing every
    /// availability interval whose <b>local date</b> (in the availability's timezone) is in
    /// <paramref name="additionalExceptionDays"/> — the SHARED-calendar holiday/closure days resolved
    /// for this resource. This is the supply-side composition: the resource's OWN exceptions
    /// (single-day + spanning, on <paramref name="availability"/>) AND the shared-calendar holidays
    /// (this set) are both whole-day removals applied at the same local-date check, so a shared
    /// holiday removes the resource's availability exactly like its own day off — and the UTC
    /// invariant is preserved (the suppression is a date filter <i>before</i> the wall-clock→UTC
    /// resolution; the surviving intervals are still differenced as absolute UTC instants).
    /// </summary>
    IReadOnlyList<TimeInterval> Expand(
        ResourceAvailability availability,
        DateTimeOffset windowStartUtc,
        DateTimeOffset windowEndUtc,
        IReadOnlySet<DateOnly> additionalExceptionDays);
}
