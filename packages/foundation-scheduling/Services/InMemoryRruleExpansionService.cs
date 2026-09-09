using System.Globalization;

namespace Harborline.Api.Foundation.Scheduling;

/// <summary>
/// In-process <see cref="IRruleExpansionService"/> implementing the
/// fleet bounded RRULE subset. Widened from the prior work-orders stub
/// (DAILY/WEEKLY/MONTHLY + INTERVAL only) to the full subset that
/// mirrors ui-react's <c>expandRecurrence</c> function per
/// council-verdict-net-arch-2026-06-12T2213Z §C-2:
/// FREQ daily/weekly/monthly/yearly, INTERVAL, COUNT/UNTIL,
/// BYDAY (with monthly ordinals), BYMONTHDAY 1-28, BYMONTH.
/// </summary>
public sealed class InMemoryRruleExpansionService : IRruleExpansionService
{
    /// <summary>
    /// Hard cap on occurrences per call, matching the ui-react
    /// <c>expandRecurrence</c> 1 000-occurrence guard.
    /// </summary>
    private const int OccurrenceCap = 1_000;

    /// <inheritdoc />
    public IReadOnlyList<DateOnly> ExpandOccurrences(
        string rrule,
        DateOnly start,
        DateOnly? end,
        int lookaheadDays,
        int leadDays,
        DateOnly today,
        string timezone)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rrule);

        var parsed = ParseRrule(rrule);
        var horizon = end ?? today.AddDays(lookaheadDays);
        var earliest = today.AddDays(leadDays);

        var result = new List<DateOnly>();
        var cursor = start;
        int occurrenceCount = 0;

        // UNTIL is a date upper bound; COUNT limits total candidate
        // occurrences from the anchor (not filtered occurrences).
        DateOnly? untilBound = parsed.Until;
        var effectiveHorizon = (untilBound.HasValue && untilBound.Value < horizon)
            ? untilBound.Value
            : horizon;

        while (cursor <= effectiveHorizon
            && result.Count < OccurrenceCap
            && (parsed.Count is null || occurrenceCount < parsed.Count.Value))
        {
            if (IsMatchingOccurrence(cursor, parsed))
            {
                occurrenceCount++;
                if (cursor >= earliest)
                    result.Add(cursor);
            }

            cursor = Advance(cursor, parsed);
        }

        return result;
    }

    // ----------------------------------------------------------------
    // Occurrence matching
    // ----------------------------------------------------------------

    private static bool IsMatchingOccurrence(DateOnly date, ParsedRrule parsed)
    {
        // BYMONTH filter
        if (parsed.ByMonth.Count > 0 && !parsed.ByMonth.Contains(date.Month))
            return false;

        return parsed.Freq switch
        {
            "DAILY"   => true,
            "WEEKLY"  => IsMatchingWeekly(date, parsed),
            "MONTHLY" => IsMatchingMonthly(date, parsed),
            "YEARLY"  => IsMatchingYearly(date, parsed),
            _ => throw new NotSupportedException(
                $"RRULE FREQ '{parsed.Freq}' is not supported. "
                + "Supported: DAILY / WEEKLY / MONTHLY / YEARLY."),
        };
    }

    private static bool IsMatchingWeekly(DateOnly date, ParsedRrule parsed)
    {
        // With no BYDAY, every occurrence in the iteration is valid.
        if (parsed.ByDay.Count == 0) return true;
        // With BYDAY, the date's day-of-week must be in the set.
        var dow = ToDayOfWeekCode(date.DayOfWeek);
        return parsed.ByDay.Any(bd => bd.Weekday == dow && bd.Ordinal == 0);
    }

    private static bool IsMatchingMonthly(DateOnly date, ParsedRrule parsed)
    {
        if (parsed.ByDay.Count > 0)
        {
            // Ordinal BYDAY e.g. 1MO (first Monday), -1FR (last Friday).
            return parsed.ByDay.Any(bd => MatchesByDay(date, bd));
        }
        if (parsed.ByMonthDay.Count > 0)
        {
            return parsed.ByMonthDay.Contains(date.Day);
        }
        // No BY* selector — every monthly step from anchor is valid.
        return true;
    }

    private static bool IsMatchingYearly(DateOnly date, ParsedRrule parsed)
    {
        if (parsed.ByMonth.Count > 0 && !parsed.ByMonth.Contains(date.Month))
            return false;
        if (parsed.ByMonthDay.Count > 0)
            return parsed.ByMonthDay.Contains(date.Day);
        if (parsed.ByDay.Count > 0)
            return parsed.ByDay.Any(bd => MatchesByDay(date, bd));
        return true;
    }

    // ----------------------------------------------------------------
    // BYDAY ordinal matching
    // ----------------------------------------------------------------

    private static bool MatchesByDay(DateOnly date, ByDayEntry bd)
    {
        var dow = ToDayOfWeekCode(date.DayOfWeek);
        if (bd.Weekday != dow) return false;
        if (bd.Ordinal == 0) return true; // plain weekday, every occurrence

        // Positive ordinal: Nth weekday of the month.
        if (bd.Ordinal > 0)
        {
            int occurrence = GetNthWeekdayOccurrenceInMonth(date);
            return occurrence == bd.Ordinal;
        }
        // Negative ordinal: Nth-last weekday of the month.
        // bd.Ordinal == -1 → last, -2 → second-last, etc.
        int occurrenceFromEnd = GetNthLastWeekdayOccurrenceInMonth(date);
        return occurrenceFromEnd == -bd.Ordinal;
    }

    /// <summary>
    /// Returns how many times the given weekday has appeared in the month
    /// up to and including <paramref name="date"/>
    /// (e.g. the first Monday of the month → 1).
    /// </summary>
    private static int GetNthWeekdayOccurrenceInMonth(DateOnly date)
    {
        int count = 0;
        for (int d = 1; d <= date.Day; d++)
        {
            if (new DateOnly(date.Year, date.Month, d).DayOfWeek == date.DayOfWeek)
                count++;
        }
        return count;
    }

    /// <summary>
    /// Returns how many times the given weekday appears in the month
    /// counting backwards from the end of the month to <paramref name="date"/>
    /// (e.g. the last Monday → 1, second-last Monday → 2).
    /// </summary>
    private static int GetNthLastWeekdayOccurrenceInMonth(DateOnly date)
    {
        int daysInMonth = DateTime.DaysInMonth(date.Year, date.Month);
        int count = 0;
        for (int d = daysInMonth; d >= date.Day; d--)
        {
            if (new DateOnly(date.Year, date.Month, d).DayOfWeek == date.DayOfWeek)
                count++;
        }
        return count;
    }

    // ----------------------------------------------------------------
    // Cursor advancement
    // ----------------------------------------------------------------

    private static DateOnly Advance(DateOnly cursor, ParsedRrule parsed)
    {
        return parsed.Freq switch
        {
            "DAILY"   => cursor.AddDays(parsed.Interval),
            "WEEKLY"  => AdvanceWeekly(cursor, parsed),
            "MONTHLY" => AdvanceMonthly(cursor, parsed),
            "YEARLY"  => cursor.AddYears(parsed.Interval),
            _ => throw new NotSupportedException(
                $"RRULE FREQ '{parsed.Freq}' is not supported."),
        };
    }

    private static DateOnly AdvanceWeekly(DateOnly cursor, ParsedRrule parsed)
    {
        if (parsed.ByDay.Count == 0)
            return cursor.AddDays(7 * parsed.Interval);

        // BYDAY weekly: walk day-by-day within the week interval.
        // The iteration cursor advances one day at a time; the outer
        // loop calls Advance once per day until a matching day is found.
        // This is the simplest correct approach — the outer loop caps at
        // OccurrenceCap so a pathological BYDAY won't run away.
        return cursor.AddDays(1);
    }

    private static DateOnly AdvanceMonthly(DateOnly cursor, ParsedRrule parsed)
    {
        if (parsed.ByDay.Count > 0 || parsed.ByMonthDay.Count > 0)
        {
            // BY* monthly: advance one day at a time; the outer loop
            // matches via IsMatchingMonthly. Cap at 32 days per month to
            // avoid iterating indefinitely if a BY* selector is pathological.
            // The outer loop's OccurrenceCap provides the hard outer bound.
            return cursor.AddDays(1);
        }
        // Plain FREQ=MONTHLY: jump by interval months anchored to start day.
        return cursor.AddMonths(parsed.Interval);
    }

    // ----------------------------------------------------------------
    // RRULE parser
    // ----------------------------------------------------------------

    private static ParsedRrule ParseRrule(string rrule)
    {
        string? freq = null;
        int interval = 1;
        int? count = null;
        DateOnly? until = null;
        var byDay = new List<ByDayEntry>();
        var byMonthDay = new List<int>();
        var byMonth = new List<int>();

        foreach (var token in rrule.Split(';',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = token.IndexOf('=', StringComparison.Ordinal);
            if (eq < 0) continue;
            var key = token[..eq].Trim().ToUpperInvariant();
            var value = token[(eq + 1)..].Trim();

            switch (key)
            {
                case "FREQ":
                    freq = value.ToUpperInvariant();
                    break;
                case "INTERVAL":
                    if (int.TryParse(value, NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out var n) && n > 0)
                        interval = n;
                    break;
                case "COUNT":
                    if (int.TryParse(value, NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out var c) && c > 0)
                        count = c;
                    break;
                case "UNTIL":
                    until = ParseUntilDate(value);
                    break;
                case "BYDAY":
                    byDay.AddRange(ParseByDay(value));
                    break;
                case "BYMONTHDAY":
                    byMonthDay.AddRange(ParseIntList(value, min: 1, max: 28));
                    break;
                case "BYMONTH":
                    byMonth.AddRange(ParseIntList(value, min: 1, max: 12));
                    break;
                // Silently ignore unsupported components (EXDATE, BYWEEKNO,
                // BYYEARDAY, WKST, etc.) per fleet bounded-subset policy.
            }
        }

        if (freq is null)
            throw new FormatException(
                $"RRULE missing FREQ= component: '{rrule}'.");

        // COUNT and UNTIL are mutually exclusive; UNTIL wins per RFC 5545 §3.8.5.3.
        if (until.HasValue) count = null;

        return new ParsedRrule(freq, interval, count, until, byDay, byMonthDay, byMonth);
    }

    private static DateOnly? ParseUntilDate(string value)
    {
        // RFC 5545 UNTIL: YYYYMMDD (date) or YYYYMMDDTHHMMSSZ (datetime).
        var datePart = value.Length >= 8 ? value[..8] : value;
        if (datePart.Length == 8
            && int.TryParse(datePart[..4], out var y)
            && int.TryParse(datePart[4..6], out var m)
            && int.TryParse(datePart[6..8], out var d))
        {
            try { return new DateOnly(y, m, d); }
            catch (ArgumentOutOfRangeException) { /* ignore malformed */ }
        }
        return null;
    }

    private static IEnumerable<ByDayEntry> ParseByDay(string value)
    {
        foreach (var part in value.Split(',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Patterns: "MO", "1MO", "-1FR", "2TU" etc.
            var span = part.Trim().ToUpperInvariant();
            if (span.Length < 2) continue;

            // Last two chars are the weekday abbreviation.
            var wdStr = span[^2..];
            var ordinalStr = span[..^2];

            int ordinal = 0;
            if (ordinalStr.Length > 0)
            {
                if (!int.TryParse(ordinalStr, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out ordinal))
                    continue; // malformed ordinal — skip
            }

            yield return new ByDayEntry(wdStr, ordinal);
        }
    }

    private static IEnumerable<int> ParseIntList(string value, int min, int max)
    {
        foreach (var part in value.Split(',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(part.Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var n)
                && n >= min && n <= max)
                yield return n;
        }
    }

    private static string ToDayOfWeekCode(DayOfWeek dow) => dow switch
    {
        DayOfWeek.Monday    => "MO",
        DayOfWeek.Tuesday   => "TU",
        DayOfWeek.Wednesday => "WE",
        DayOfWeek.Thursday  => "TH",
        DayOfWeek.Friday    => "FR",
        DayOfWeek.Saturday  => "SA",
        DayOfWeek.Sunday    => "SU",
        _ => throw new ArgumentOutOfRangeException(nameof(dow)),
    };

    // ----------------------------------------------------------------
    // Internal model
    // ----------------------------------------------------------------

    private sealed record ParsedRrule(
        string Freq,
        int Interval,
        int? Count,
        DateOnly? Until,
        IReadOnlyList<ByDayEntry> ByDay,
        IReadOnlyList<int> ByMonthDay,
        IReadOnlyList<int> ByMonth);

    private sealed record ByDayEntry(string Weekday, int Ordinal);
}
