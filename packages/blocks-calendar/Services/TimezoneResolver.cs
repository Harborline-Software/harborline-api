namespace Harborline.Api.Blocks.Calendar.Services;

/// <summary>
/// Resolves a wall-clock local date+time in an IANA timezone to a UTC <see cref="DateTimeOffset"/>,
/// applying DST and the two RFC-5545 DST edge policies. Slice S1 — the time-of-day + timezone/DST
/// layer that sits <i>on top of</i> the date-granular RRULE expansion (the shared expander stays
/// date-only; this converts each generated date+time-of-day to a real instant).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a thin helper, not Ical.Net.</b> The fleet RRULE expander
/// (<c>IRruleExpansionService</c>) deliberately mirrors the ui-react <c>expandRecurrence</c>
/// function exactly (council-verdict-net-arch §C-2) so the frontend Scheduler and the backend
/// produce the <i>same occurrence set</i>. Swapping in Ical.Net for the calendar block would create
/// a second, divergent RRULE engine. Instead we keep the shared expander's date output and apply
/// time-of-day + DST here with <see cref="TimeZoneInfo"/> — which on .NET 11 resolves IANA ids
/// natively (ICU) and converts with full DST awareness. Both candidates are MIT; this introduces no
/// new dependency.
/// </para>
/// <para>
/// <b>DST edge policies (RFC 5545 §3.3.5 spirit):</b>
/// <list type="bullet">
///   <item><description><b>Spring-forward gap</b> (a local time that does not exist — e.g.
///   02:30 on a "skip 02:00→03:00" day):
///   <see cref="TimeZoneInfo.ConvertTimeToUtc(DateTime, TimeZoneInfo)"/> throws on an
///   invalid time, so we <i>snap the wall-clock forward</i> by the DST delta into the valid range
///   (the common calendar behavior — a 02:30 event lands at 03:30). A typical 9 a.m. event is never
///   in the gap, so this only affects events deliberately scheduled in the lost hour.</description></item>
///   <item><description><b>Fall-back ambiguity</b> (a local time that occurs twice): .NET resolves
///   the ambiguous time to the <i>standard</i> (later) offset by default and does not throw; we
///   accept that deterministic resolution.</description></item>
/// </list>
/// </para>
/// </remarks>
public static class TimezoneResolver
{
    /// <summary>
    /// Resolve <paramref name="ianaTimezone"/> to a <see cref="TimeZoneInfo"/>. Throws
    /// <see cref="TimeZoneNotFoundException"/> for an unknown id (callers should validate the IANA
    /// id at event-create time; the entity already requires a non-empty tz).
    /// </summary>
    public static TimeZoneInfo Resolve(string ianaTimezone)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ianaTimezone);
        return TimeZoneInfo.FindSystemTimeZoneById(ianaTimezone);
    }

    /// <summary>
    /// Convert a wall-clock <paramref name="localDate"/> + <paramref name="localTime"/> in
    /// <paramref name="tz"/> to a UTC <see cref="DateTimeOffset"/>, applying DST. Spring-forward
    /// gap times are snapped forward by the DST delta; fall-back ambiguous times resolve to the
    /// standard offset (no throw).
    /// </summary>
    public static DateTimeOffset ToUtcInstant(DateOnly localDate, TimeOnly localTime, TimeZoneInfo tz)
    {
        ArgumentNullException.ThrowIfNull(tz);

        var wall = localDate.ToDateTime(localTime, DateTimeKind.Unspecified);

        // Spring-forward gap: the local time does not exist. Snap forward by the DST delta so the
        // event lands in the valid range instead of throwing (calendar-standard behavior).
        if (tz.IsInvalidTime(wall))
        {
            var delta = GapDelta(wall, tz);
            wall = wall.Add(delta);
        }

        // ConvertTimeToUtc resolves a fall-back-ambiguous time to the standard offset (deterministic,
        // no throw) and applies DST for every other time.
        var utc = TimeZoneInfo.ConvertTimeToUtc(wall, tz);
        return new DateTimeOffset(utc, TimeSpan.Zero);
    }

    /// <summary>
    /// The DST adjustment delta covering an invalid (skipped) local time — the size of the
    /// spring-forward gap (typically +1h). Derived from the rule whose
    /// <see cref="TimeZoneInfo.AdjustmentRule.DaylightTransitionStart"/> brackets the date, falling
    /// back to one hour (the universal DST shift) if no rule is found.
    /// </summary>
    private static TimeSpan GapDelta(DateTime invalidWall, TimeZoneInfo tz)
    {
        foreach (var rule in tz.GetAdjustmentRules())
        {
            if (invalidWall.Date >= rule.DateStart.Date && invalidWall.Date <= rule.DateEnd.Date)
                return rule.DaylightDelta;
        }
        return TimeSpan.FromHours(1);
    }
}
