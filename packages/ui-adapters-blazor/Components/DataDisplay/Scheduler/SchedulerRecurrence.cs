using System.Globalization;
using Harborline.Api.Foundation.Models;

namespace Harborline.Api.UIAdapters.Blazor.Components.DataDisplay.Scheduler;

/// <summary>
/// Expands the bounded RRULE subset used by the Scheduler gallery.
/// </summary>
public static class SchedulerRecurrence
{
    /// <summary>Hard renderer guard for open-ended or unexpectedly dense series.</summary>
    public const int MaxOccurrences = 1_000;

    private static readonly IReadOnlyDictionary<string, DayOfWeek> DayCodes =
        new Dictionary<string, DayOfWeek>(StringComparer.OrdinalIgnoreCase)
        {
            ["SU"] = DayOfWeek.Sunday,
            ["MO"] = DayOfWeek.Monday,
            ["TU"] = DayOfWeek.Tuesday,
            ["WE"] = DayOfWeek.Wednesday,
            ["TH"] = DayOfWeek.Thursday,
            ["FR"] = DayOfWeek.Friday,
            ["SA"] = DayOfWeek.Saturday,
        };

    /// <summary>Returns <see langword="true"/> when an appointment carries a recurrence rule.</summary>
    public static bool IsRecurring(SchedulerAppointment appointment)
        => !string.IsNullOrWhiteSpace(appointment.RecurrenceRule);

    /// <summary>
    /// Expands a series into concrete appointments whose start date is inside the inclusive
    /// date window. Non-recurring appointments are returned unchanged so callers can use this
    /// method as a single normalization step.
    /// </summary>
    public static IEnumerable<SchedulerAppointment> Expand(
        SchedulerAppointment master,
        DateOnly windowStart,
        DateOnly windowEnd)
    {
        if (windowEnd < windowStart)
        {
            yield break;
        }

        if (!IsRecurring(master))
        {
            yield return master;
            yield break;
        }

        var rule = ParseRule(master.RecurrenceRule!);
        if (rule is null)
        {
            // Keep malformed series visible as the source record. This is safer for a UI than
            // silently deleting an appointment the consumer supplied.
            yield return master;
            yield break;
        }

        var duration = master.End - master.Start;
        var emitted = 0;
        foreach (var occurrenceStart in EnumerateStarts(master.Start, rule.Value))
        {
            if (emitted >= MaxOccurrences)
            {
                yield break;
            }

            if (rule.Value.Count is int count && emitted >= count)
            {
                yield break;
            }

            if (rule.Value.Until is DateTime until && occurrenceStart > until)
            {
                yield break;
            }

            emitted++;
            var occurrenceDate = DateOnly.FromDateTime(occurrenceStart);
            if (occurrenceDate > windowEnd)
            {
                yield break;
            }

            if (occurrenceDate < windowStart || IsException(master, occurrenceStart))
            {
                continue;
            }

            yield return new SchedulerRecurrenceOccurrence
            {
                Id = $"{master.Id}::{occurrenceStart:yyyyMMdd'T'HHmmss}",
                SeriesId = master.Id,
                OriginalStart = occurrenceStart,
                Title = master.Title,
                Description = master.Description,
                Start = occurrenceStart,
                End = occurrenceStart + duration,
                IsAllDay = master.IsAllDay,
                Color = master.Color,
            };
        }
    }

    private static bool IsException(SchedulerAppointment master, DateTime occurrenceStart)
        => master.RecurrenceExceptions?.Any(exception =>
            exception.Date == occurrenceStart.Date &&
            (exception.TimeOfDay == TimeSpan.Zero || exception.TimeOfDay == occurrenceStart.TimeOfDay)) == true;

    private readonly record struct Rule(
        string Frequency,
        int Interval,
        int? Count,
        DateTime? Until,
        IReadOnlyList<DayOfWeek> ByDays,
        IReadOnlyList<(int Ordinal, DayOfWeek Day)> ByDayOrdinals,
        IReadOnlyList<int> ByMonthDays,
        IReadOnlyList<int> ByMonths);

    private static Rule? ParseRule(string text)
    {
        string? frequency = null;
        var interval = 1;
        int? count = null;
        DateTime? until = null;
        var byDays = new List<DayOfWeek>();
        var byDayOrdinals = new List<(int Ordinal, DayOfWeek Day)>();
        var byMonthDays = new List<int>();
        var byMonths = new List<int>();

        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = part[..separator].Trim().ToUpperInvariant();
            var value = part[(separator + 1)..].Trim();
            switch (key)
            {
                case "FREQ":
                    frequency = value.ToUpperInvariant();
                    break;
                case "INTERVAL":
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedInterval)
                        || parsedInterval < 1)
                    {
                        return null;
                    }

                    interval = parsedInterval;
                    break;
                case "COUNT":
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedCount)
                        || parsedCount < 1)
                    {
                        return null;
                    }

                    count = parsedCount;
                    break;
                case "UNTIL":
                    if (!TryParseUntil(value, out until))
                    {
                        return null;
                    }

                    break;
                case "BYDAY":
                    foreach (var token in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (token.Length < 2 || !DayCodes.TryGetValue(token[^2..], out var day))
                        {
                            return null;
                        }

                        var ordinalText = token[..^2];
                        if (ordinalText.Length > 0)
                        {
                            if (!int.TryParse(ordinalText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ordinal)
                                || ordinal == 0)
                            {
                                return null;
                            }

                            byDayOrdinals.Add((ordinal, day));
                        }
                        else
                        {
                            byDays.Add(day);
                        }
                    }

                    break;
                case "BYMONTHDAY":
                    foreach (var token in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dayOfMonth)
                            || dayOfMonth is < -31 or 0 or > 31)
                        {
                            // Support both positive and negative month-day ordinals.
                            return null;
                        }

                        byMonthDays.Add(dayOfMonth);
                    }

                    break;
                case "BYMONTH":
                    foreach (var token in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var month)
                            || month is < 1 or > 12)
                        {
                            return null;
                        }

                        byMonths.Add(month);
                    }

                    break;
            }
        }

        if (frequency is not ("DAILY" or "WEEKLY" or "MONTHLY" or "YEARLY") || (count.HasValue && until.HasValue))
        {
            return null;
        }

        return new Rule(
            frequency,
            interval,
            count,
            until,
            byDays.Distinct().OrderBy(day => (int)day).ToArray(),
            byDayOrdinals.Distinct().OrderBy(item => item.Ordinal).ThenBy(item => item.Day).ToArray(),
            byMonthDays.Distinct().OrderBy(day => day).ToArray(),
            byMonths.Distinct().OrderBy(month => month).ToArray());
    }

    private static bool TryParseUntil(string value, out DateTime? until)
    {
        until = null;
        var formats = new[] { "yyyyMMdd'T'HHmmss'Z'", "yyyyMMdd'T'HHmmss", "yyyyMMdd" };
        if (!DateTime.TryParseExact(value, formats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            return false;
        }

        until = value.Length == 8 ? parsed.Date.AddDays(1).AddTicks(-1) : parsed;
        return true;
    }

    private static IEnumerable<DateTime> EnumerateStarts(DateTime seriesStart, Rule rule)
    {
        var time = seriesStart.TimeOfDay;
        switch (rule.Frequency)
        {
            case "DAILY":
                for (var index = 0; index < MaxOccurrences * 32; index++)
                {
                    var occurrence = seriesStart.AddDays(index * rule.Interval);
                    if (MatchesFilters(occurrence, rule))
                    {
                        yield return occurrence;
                    }
                }

                break;
            case "WEEKLY":
            {
                var days = rule.ByDays.Count > 0
                    ? rule.ByDays
                    : new[] { seriesStart.DayOfWeek };
                var weekAnchor = seriesStart.Date.AddDays(-(int)seriesStart.DayOfWeek);
                for (var week = 0; week < MaxOccurrences * 8; week++)
                {
                    var weekStart = weekAnchor.AddDays(week * 7 * rule.Interval);
                    foreach (var day in days)
                    {
                        var occurrence = weekStart.AddDays((int)day) + time;
                        if (occurrence >= seriesStart && MatchesFilters(occurrence, rule))
                        {
                            yield return occurrence;
                        }
                    }
                }

                break;
            }
            case "MONTHLY":
                for (var month = 0; month < MaxOccurrences; month++)
                {
                    var monthFirst = new DateTime(seriesStart.Year, seriesStart.Month, 1)
                        .AddMonths(month * rule.Interval);
                    if (rule.ByMonths.Count > 0 && !rule.ByMonths.Contains(monthFirst.Month))
                    {
                        continue;
                    }

                    foreach (var date in MonthlyDates(monthFirst, seriesStart, rule))
                    {
                        yield return date + time;
                    }
                }

                break;
            case "YEARLY":
                for (var yearIndex = 0; yearIndex < MaxOccurrences; yearIndex++)
                {
                    var year = seriesStart.Year + yearIndex * rule.Interval;
                    var months = rule.ByMonths.Count > 0 ? rule.ByMonths : new[] { seriesStart.Month };
                    foreach (var month in months)
                    {
                        var monthFirst = new DateTime(year, month, 1);
                        foreach (var date in YearlyDates(monthFirst, seriesStart, rule))
                        {
                            var occurrence = date + time;
                            if (occurrence >= seriesStart)
                            {
                                yield return occurrence;
                            }
                        }
                    }
                }

                break;
        }
    }

    private static IEnumerable<DateTime> MonthlyDates(DateTime monthFirst, DateTime seriesStart, Rule rule)
    {
        var daysInMonth = DateTime.DaysInMonth(monthFirst.Year, monthFirst.Month);
        if (rule.ByDayOrdinals.Count > 0)
        {
            foreach (var ordinal in rule.ByDayOrdinals)
            {
                var ordinalDate = NthWeekdayOfMonth(monthFirst, ordinal.Ordinal, ordinal.Day);
                if (ordinalDate.HasValue && ordinalDate.Value >= seriesStart.Date)
                {
                    yield return ordinalDate.Value;
                }
            }

            yield break;
        }

        if (rule.ByDays.Count > 0)
        {
            for (var day = 1; day <= daysInMonth; day++)
            {
                var date = new DateTime(monthFirst.Year, monthFirst.Month, day);
                if (rule.ByDays.Contains(date.DayOfWeek) && date >= seriesStart.Date)
                {
                    yield return date;
                }
            }

            yield break;
        }

        var days = rule.ByMonthDays.Count > 0 ? rule.ByMonthDays : new[] { seriesStart.Day };
        foreach (var day in days)
        {
            var resolvedDay = ResolveMonthDay(day, daysInMonth);
            if (resolvedDay.HasValue)
            {
                var date = new DateTime(monthFirst.Year, monthFirst.Month, resolvedDay.Value);
                if (date >= seriesStart.Date)
                {
                    yield return date;
                }
            }
        }
    }

    private static IEnumerable<DateTime> YearlyDates(DateTime monthFirst, DateTime seriesStart, Rule rule)
    {
        var daysInMonth = DateTime.DaysInMonth(monthFirst.Year, monthFirst.Month);
        if (rule.ByDayOrdinals.Count > 0)
        {
            foreach (var ordinal in rule.ByDayOrdinals)
            {
                var date = NthWeekdayOfMonth(monthFirst, ordinal.Ordinal, ordinal.Day);
                if (date.HasValue && date.Value >= seriesStart.Date)
                {
                    yield return date.Value;
                }
            }

            yield break;
        }

        if (rule.ByDays.Count > 0)
        {
            for (var day = 1; day <= daysInMonth; day++)
            {
                var date = new DateTime(monthFirst.Year, monthFirst.Month, day);
                if (rule.ByDays.Contains(date.DayOfWeek) && date >= seriesStart.Date)
                {
                    yield return date;
                }
            }

            yield break;
        }

        var days = rule.ByMonthDays.Count > 0 ? rule.ByMonthDays : new[] { seriesStart.Day };
        foreach (var day in days)
        {
            var resolvedDay = ResolveMonthDay(day, daysInMonth);
            if (resolvedDay.HasValue)
            {
                var date = new DateTime(monthFirst.Year, monthFirst.Month, resolvedDay.Value);
                if (date >= seriesStart.Date)
                {
                    yield return date;
                }
            }
        }
    }

    private static bool MatchesFilters(DateTime date, Rule rule)
    {
        if (rule.ByMonths.Count > 0 && !rule.ByMonths.Contains(date.Month))
        {
            return false;
        }

        if (rule.ByMonthDays.Count > 0 && !rule.ByMonthDays.Any(day => ResolveMonthDay(day, DateTime.DaysInMonth(date.Year, date.Month)) == date.Day))
        {
            return false;
        }

        if (rule.ByDays.Count > 0 && !rule.ByDays.Contains(date.DayOfWeek))
        {
            return false;
        }

        return rule.ByDayOrdinals.Count == 0 || rule.ByDayOrdinals.Any(item =>
            item.Day == date.DayOfWeek && IsMatchingOrdinal(date, item.Ordinal));
    }

    private static bool IsMatchingOrdinal(DateTime date, int ordinal)
    {
        if (ordinal > 0)
        {
            return ((date.Day - 1) / 7) + 1 == ordinal;
        }

        var daysFromEnd = DateTime.DaysInMonth(date.Year, date.Month) - date.Day;
        return -((daysFromEnd / 7) + 1) == ordinal;
    }

    private static int? ResolveMonthDay(int day, int daysInMonth)
    {
        var resolved = day > 0 ? day : daysInMonth + day + 1;
        return resolved >= 1 && resolved <= 31 && resolved <= daysInMonth ? resolved : null;
    }

    private static DateTime? NthWeekdayOfMonth(DateTime monthFirst, int ordinal, DayOfWeek day)
    {
        var daysInMonth = DateTime.DaysInMonth(monthFirst.Year, monthFirst.Month);
        if (ordinal > 0)
        {
            var offset = ((int)day - (int)monthFirst.DayOfWeek + 7) % 7;
            var dayOfMonth = 1 + offset + (ordinal - 1) * 7;
            return dayOfMonth <= daysInMonth ? new DateTime(monthFirst.Year, monthFirst.Month, dayOfMonth) : null;
        }

        var last = new DateTime(monthFirst.Year, monthFirst.Month, daysInMonth);
        var backOffset = ((int)last.DayOfWeek - (int)day + 7) % 7;
        var negativeDay = daysInMonth - backOffset + (ordinal + 1) * 7;
        return negativeDay >= 1 ? new DateTime(monthFirst.Year, monthFirst.Month, negativeDay) : null;
    }
}

/// <summary>
/// A rendered occurrence that retains its series identity and original slot for a consumer that
/// needs to persist an occurrence-level edit.
/// </summary>
public sealed class SchedulerRecurrenceOccurrence : SchedulerAppointment
{
    /// <summary>The master appointment identifier.</summary>
    public string SeriesId { get; init; } = string.Empty;

    /// <summary>The generated slot before any occurrence-level edit.</summary>
    public DateTime OriginalStart { get; init; }

    /// <summary>Whether this record is an occurrence override rather than generated output.</summary>
    public bool IsException { get; init; }
}
