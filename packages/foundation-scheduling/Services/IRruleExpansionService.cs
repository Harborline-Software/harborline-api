namespace Harborline.Api.Foundation.Scheduling;

/// <summary>
/// Expand an RFC 5545 RRULE string into a concrete sequence of due
/// dates within a configurable window.
/// </summary>
/// <remarks>
/// <para>
/// <b>Strict shared RRULE producer.</b> This API facade delegates to the pinned Platform
/// <c>Harborline.Foundation.Scheduling</c> implementation. The producer defines the supported
/// RFC 5545 subset; this facade does not retain a separate parser or claim exact equivalence
/// to a UI implementation.
/// </para>
/// <para>
/// The shared subset includes DAILY/WEEKLY/MONTHLY/YEARLY frequency and supported INTERVAL,
/// COUNT/UNTIL and BY* selectors. Combination applicability, value bounds and the complete-or-refusal
/// occurrence limit belong to that producer, not to copied API parsing rules.
/// </para>
/// <para>
/// Unsupported RRULE components (EXDATE, BYWEEKNO, BYYEARDAY, etc.)
/// are refused by the shared Platform producer so an authored restriction cannot be silently discarded.
/// Malformed values of supported parts are refused rather than defaulted or dropped.
/// </para>
/// <para>
/// The result is complete or an exception is propagated. When the requested range would exceed the
/// producer's 1 000-occurrence limit, it throws
/// <see cref="Harborline.Foundation.Scheduling.RruleExpansionCapExceededException"/> instead of returning
/// an indistinguishable partial list. The producer's exception and its range metadata are preserved.
/// </para>
/// </remarks>
public interface IRruleExpansionService
{
    /// <summary>
    /// Return all occurrence dates that fall within the configured
    /// window per the parsed <paramref name="rrule"/>.
    /// </summary>
    /// <param name="rrule">
    /// RFC 5545 RRULE string (e.g. <c>FREQ=MONTHLY;BYMONTHDAY=1</c>).
    /// </param>
    /// <param name="start">Recurrence anchor / DTSTART date.</param>
    /// <param name="end">
    /// Optional hard upper bound (e.g. <c>RecurringSchedule.EndsOn</c>).
    /// When <see langword="null"/>, the horizon is
    /// <c>today + <paramref name="lookaheadDays"/></c>.
    /// </param>
    /// <param name="lookaheadDays">
    /// Soft upper bound (days from <paramref name="today"/>) used when
    /// <paramref name="end"/> is <see langword="null"/>.
    /// </param>
    /// <param name="leadDays">
    /// Skip occurrences that fall earlier than
    /// <c>today + leadDays</c>. Enables "generate work N days ahead"
    /// semantics without changing the recurrence anchor.
    /// </param>
    /// <param name="today">
    /// Wall-clock today. Callers inject this so unit tests are
    /// deterministic without mocking <c>DateTime.UtcNow</c>.
    /// </param>
    /// <param name="timezone">
    /// IANA timezone id for the schedule owner (e.g. <c>America/Los_Angeles</c>).
    /// The pinned producer accepts this coordinate without timezone conversion; this facade
    /// forwards it unchanged and introduces no timezone policy.
    /// </param>
    /// <returns>
    /// Occurrences in ascending date order, filtered by
    /// <paramref name="leadDays"/> and bounded by <paramref name="end"/>
    /// (or <c>today + lookaheadDays</c>). Never exceeds 1 000 items and never returns a capped partial result.
    /// </returns>
    /// <exception cref="FormatException">
    /// Thrown when <paramref name="rrule"/> is missing a <c>FREQ=</c> component or a supported part is malformed.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="rrule"/> is null or whitespace.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// Thrown for a component or combination outside the producer's supported subset.
    /// </exception>
    /// <exception cref="Harborline.Foundation.Scheduling.RruleExpansionCapExceededException">
    /// Thrown when the producer cannot return a complete expansion within its occurrence limit.
    /// </exception>
    IReadOnlyList<DateOnly> ExpandOccurrences(
        string rrule,
        DateOnly start,
        DateOnly? end,
        int lookaheadDays,
        int leadDays,
        DateOnly today,
        string timezone);
}
