using Harborline.Api.Blocks.Calendar.Models;

namespace Harborline.Api.Blocks.Calendar.Services;

/// <summary>
/// Interval algebra over half-open UTC <see cref="TimeInterval"/>s (Slice S3) — merge, subtract,
/// clip. The shared engine of free/busy: <c>free = clip(merge(availability) − merge(occupancy))</c>.
/// All operations treat intervals as half-open <c>[Start, End)</c> so touching endpoints never
/// spuriously overlap (a 9–12 and a 12–5 are adjacent, not overlapping).
/// </summary>
internal static class IntervalMath
{
    /// <summary>
    /// Merge a set of intervals into the minimal set of non-overlapping, non-adjacent intervals
    /// covering the same instants (the union), ordered by start. Adjacent intervals (one ends where
    /// the next begins) are coalesced into one.
    /// </summary>
    public static List<TimeInterval> Merge(IEnumerable<TimeInterval> intervals)
    {
        var sorted = intervals.OrderBy(i => i.StartUtc).ThenBy(i => i.EndUtc).ToList();
        var merged = new List<TimeInterval>();
        foreach (var iv in sorted)
        {
            if (merged.Count == 0)
            {
                merged.Add(iv);
                continue;
            }
            var last = merged[^1];
            // Coalesce when the next interval starts at or before the running end (overlap OR adjacency).
            if (iv.StartUtc <= last.EndUtc)
            {
                if (iv.EndUtc > last.EndUtc)
                    merged[^1] = new TimeInterval(last.StartUtc, iv.EndUtc);
                // else fully contained — drop.
            }
            else
            {
                merged.Add(iv);
            }
        }
        return merged;
    }

    /// <summary>
    /// Subtract the union of <paramref name="busy"/> from the union of <paramref name="available"/> —
    /// the free intervals (availability with the busy spans punched out), ordered by start. Both
    /// operands are merged first; the result contains only positive-length intervals.
    /// </summary>
    public static List<TimeInterval> Subtract(IEnumerable<TimeInterval> available, IEnumerable<TimeInterval> busy)
    {
        var avail = Merge(available);
        var blocked = Merge(busy);
        var free = new List<TimeInterval>();

        foreach (var slot in avail)
        {
            var cursor = slot.StartUtc;
            foreach (var b in blocked)
            {
                if (b.EndUtc <= cursor) continue;          // entirely before the cursor
                if (b.StartUtc >= slot.EndUtc) break;      // past this slot (blocked is sorted)

                if (b.StartUtc > cursor)
                    free.Add(new TimeInterval(cursor, b.StartUtc));   // gap before the block

                if (b.EndUtc > cursor)
                    cursor = b.EndUtc;                                // advance past the block
                if (cursor >= slot.EndUtc) break;
            }
            if (cursor < slot.EndUtc)
                free.Add(new TimeInterval(cursor, slot.EndUtc));      // tail of the slot
        }
        return free;
    }

    /// <summary>
    /// Clip an interval to [<paramref name="windowStart"/>, <paramref name="windowEnd"/>] — the
    /// overlap, or <see langword="null"/> when the interval falls entirely outside the window.
    /// </summary>
    public static TimeInterval? Clip(TimeInterval iv, DateTimeOffset windowStart, DateTimeOffset windowEnd)
    {
        var start = iv.StartUtc > windowStart ? iv.StartUtc : windowStart;
        var end = iv.EndUtc < windowEnd ? iv.EndUtc : windowEnd;
        return end > start ? new TimeInterval(start, end) : null;
    }

    /// <summary>Clip every interval to the window, dropping those that fall entirely outside it.</summary>
    public static List<TimeInterval> ClipAll(IEnumerable<TimeInterval> intervals, DateTimeOffset windowStart, DateTimeOffset windowEnd)
    {
        var result = new List<TimeInterval>();
        foreach (var iv in intervals)
        {
            if (Clip(iv, windowStart, windowEnd) is { } clipped)
                result.Add(clipped);
        }
        return result;
    }
}
