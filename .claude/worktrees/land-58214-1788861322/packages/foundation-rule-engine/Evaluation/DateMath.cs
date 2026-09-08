using System.Globalization;

namespace Harborline.Api.Foundation.RuleEngine.Evaluation;

/// <summary>
/// Deterministic, timezone-free calendar arithmetic for the <c>date.*</c>
/// operators (SPINE-1 design §1.4). Dates are <c>YYYY-MM-DD</c> in the proleptic
/// Gregorian calendar (UTC, date-only — DST is structurally excluded). Uses
/// Howard Hinnant's days-from-civil algorithm so leap years are exact and the
/// result is <b>byte-identical</b> to the TS tier's port.
/// </summary>
internal static class DateMath
{
    /// <summary>Parses a <c>YYYY-MM-DD</c> date into (year, month, day); throws on malformed input.</summary>
    public static (int Y, int M, int D) Parse(string text)
    {
        if (text is null || text.Length < 8) throw new FormatException($"invalid date '{text}'");
        var parts = text.Split('-');
        // Support an optional time suffix by truncating at 'T'.
        if (parts.Length >= 3)
        {
            var dayPart = parts[2];
            int tIdx = dayPart.IndexOf('T');
            if (tIdx >= 0) dayPart = dayPart[..tIdx];
            if (int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int y)
                && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int m)
                && int.TryParse(dayPart, NumberStyles.None, CultureInfo.InvariantCulture, out int d)
                && m is >= 1 and <= 12 && d is >= 1 and <= 31)
            {
                return (y, m, d);
            }
        }
        throw new FormatException($"invalid date '{text}'");
    }

    /// <summary>Howard Hinnant's days_from_civil: serial day number (1970-01-01 = 0).</summary>
    public static long EpochDay(int y, int m, int d)
    {
        long yy = m <= 2 ? y - 1 : y;
        long era = (yy >= 0 ? yy : yy - 399) / 400;
        long yoe = yy - era * 400;                                  // [0, 399]
        long doy = (153 * (m > 2 ? m - 3 : m + 9) + 2) / 5 + d - 1; // [0, 365]
        long doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;           // [0, 146096]
        return era * 146097 + doe - 719468;
    }

    /// <summary>Inverse of <see cref="EpochDay"/>: civil date from a serial day number.</summary>
    public static (int Y, int M, int D) FromEpochDay(long z)
    {
        z += 719468;
        long era = (z >= 0 ? z : z - 146096) / 146097;
        long doe = z - era * 146097;                                  // [0, 146096]
        long yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365; // [0, 399]
        long y = yoe + era * 400;
        long doy = doe - (365 * yoe + yoe / 4 - yoe / 100);           // [0, 365]
        long mp = (5 * doy + 2) / 153;                                 // [0, 11]
        long d = doy - (153 * mp + 2) / 5 + 1;                         // [1, 31]
        long m = mp < 10 ? mp + 3 : mp - 9;                            // [1, 12]
        return ((int)(m <= 2 ? y + 1 : y), (int)m, (int)d);
    }

    private static int DaysInMonth(int y, int m)
    {
        int[] lengths = { 31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31 };
        if (m == 2 && IsLeap(y)) return 29;
        return lengths[m - 1];
    }

    private static bool IsLeap(int y) => (y % 4 == 0 && y % 100 != 0) || y % 400 == 0;

    private static string Format(int y, int m, int d)
        => $"{y:D4}-{m:D2}-{d:D2}";

    /// <summary>Today's UTC date (from an injected clock) as <c>YYYY-MM-DD</c>.</summary>
    public static string Today(DateTimeOffset nowUtc)
        => Format(nowUtc.UtcDateTime.Year, nowUtc.UtcDateTime.Month, nowUtc.UtcDateTime.Day);

    /// <summary>Adds <paramref name="n"/> of <paramref name="unit"/> (day|month|year) with day-clamping.</summary>
    public static string Add(string date, long n, string unit)
    {
        var (y, m, d) = Parse(date);
        switch (unit)
        {
            case "day":
                var (ny, nm, nd) = FromEpochDay(EpochDay(y, m, d) + n);
                return Format(ny, nm, nd);
            case "month":
            {
                long total = (long)y * 12 + (m - 1) + n;
                int yr = (int)Math.DivRem(total, 12, out long mo);
                if (mo < 0) { mo += 12; yr -= 1; }
                int month = (int)mo + 1;
                int day = Math.Min(d, DaysInMonth(yr, month));
                return Format(yr, month, day);
            }
            case "year":
            {
                int yr = y + (int)n;
                int day = Math.Min(d, DaysInMonth(yr, m));
                return Format(yr, m, day);
            }
            default:
                throw new FormatException($"unknown date unit '{unit}'");
        }
    }

    /// <summary>Difference <c>a - b</c> in whole days (v1 supports the <c>day</c> unit).</summary>
    public static long DiffDays(string a, string b)
    {
        var (ay, am, ad) = Parse(a);
        var (by, bm, bd) = Parse(b);
        return EpochDay(ay, am, ad) - EpochDay(by, bm, bd);
    }
}
