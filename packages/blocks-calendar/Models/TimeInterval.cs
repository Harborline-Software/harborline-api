namespace Harborline.Api.Blocks.Calendar.Models;

/// <summary>
/// A half-open UTC time interval <c>[StartUtc, EndUtc)</c> (Slice S3) — the common shape of an
/// availability span, a busy span, and a free slot in the free/busy computation. Half-open so
/// adjacent intervals (one ends exactly where the next begins) do not spuriously overlap — a 9–12
/// block and a 12–5 block are back-to-back, not overlapping.
/// </summary>
/// <param name="StartUtc">Inclusive start instant (UTC).</param>
/// <param name="EndUtc">Exclusive end instant (UTC) — strictly after <paramref name="StartUtc"/>.</param>
public readonly record struct TimeInterval(DateTimeOffset StartUtc, DateTimeOffset EndUtc)
{
    /// <summary>Create a validated interval; throws when <paramref name="endUtc"/> is at or before <paramref name="startUtc"/>.</summary>
    public static TimeInterval Of(DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        if (endUtc <= startUtc)
            throw new ArgumentException("Interval EndUtc must be strictly after StartUtc.", nameof(endUtc));
        return new TimeInterval(startUtc, endUtc);
    }

    /// <summary>The interval's duration.</summary>
    public TimeSpan Duration => EndUtc - StartUtc;

    /// <summary>
    /// True when this interval and <paramref name="other"/> share any positive-length span (half-open
    /// overlap — touching endpoints do not count as overlap).
    /// </summary>
    public bool Overlaps(TimeInterval other) => StartUtc < other.EndUtc && other.StartUtc < EndUtc;

    /// <summary>True when <paramref name="instant"/> lies within <c>[StartUtc, EndUtc)</c>.</summary>
    public bool Contains(DateTimeOffset instant) => instant >= StartUtc && instant < EndUtc;
}
