namespace Harborline.Api.Foundation.Scheduling;

/// <summary>
/// Expand an RFC 5545 RRULE string into a concrete sequence of due
/// dates within a configurable window.
/// </summary>
/// <remarks>
/// <para>
/// <b>Fleet bounded RRULE subset</b> — this contract mirrors the
/// ui-react <c>expandRecurrence</c> function in
/// <c>SchedulerRecurrence.ts</c> exactly so the same RRULE configured
/// in the Scheduler UI expands to the same occurrence set on the
/// backend. Cross-tier consistency is a binding requirement per
/// council-verdict-net-arch-2026-06-12T2213Z §C-2.
/// </para>
/// <para>
/// <b>Supported subset:</b>
/// <list type="bullet">
///   <item><description><c>FREQ</c> — DAILY | WEEKLY | MONTHLY | YEARLY</description></item>
///   <item><description><c>INTERVAL=N</c> — positive integer (default 1)</description></item>
///   <item><description><c>COUNT=N</c> or <c>UNTIL=YYYYMMDD</c> (mutually exclusive; UNTIL wins when both present)</description></item>
///   <item><description><c>BYDAY</c> — weekday list e.g. <c>MO,WE,FR</c>; with monthly ordinal e.g. <c>1MO</c> (first Monday), <c>-1FR</c> (last Friday)</description></item>
///   <item><description><c>BYMONTHDAY</c> — calendar day 1–28 (capped at 28 to avoid month-end ambiguity across February and 30-day months)</description></item>
///   <item><description><c>BYMONTH</c> — month number 1–12</description></item>
///   <item><description>Hard occurrence cap: 1 000 occurrences per call to prevent runaway expansion</description></item>
/// </list>
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
    /// V1 treats all dates as naive (no timezone conversion); the
    /// parameter is accepted now so callers are timezone-aware at the
    /// call site and the Ical.Net follow-on can use it without a
    /// signature change.
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
