namespace Harborline.Api.Blocks.Calendar.Models;

/// <summary>
/// The result of a free/busy query for one resource over a UTC window (Slice S3) —
/// <c>free = availability windows − ALL occupancy (Bookable + Blocking + Tentative)</c>. THE query
/// that makes booking and coverage work: a booking path picks a <see cref="FreeSlots"/> entry; a
/// coverage overlay reads the same classified data via the by-context query.
/// </summary>
/// <remarks>
/// <para>
/// Both lists are clipped to the requested window and ordered by start. <see cref="FreeSlots"/> are
/// the bookable gaps (availability minus busy); <see cref="BusyIntervals"/> are the occupied spans
/// (the merged occupancy that fell inside availability). A resource with no availability record has
/// empty <see cref="FreeSlots"/> (nothing is bookable) — availability is the supply that must exist
/// first.
/// </para>
/// </remarks>
/// <param name="ResourceRef">The resource (Party or Asset) this free/busy was computed for.</param>
/// <param name="WindowStartUtc">The query window start (UTC).</param>
/// <param name="WindowEndUtc">The query window end (UTC).</param>
/// <param name="FreeSlots">The bookable free intervals — availability minus all occupancy, clipped to the window, ordered by start.</param>
/// <param name="BusyIntervals">The busy intervals (the occupancy that overlapped availability), merged, ordered by start.</param>
public sealed record FreeBusyResult(
    ParticipantRef ResourceRef,
    DateTimeOffset WindowStartUtc,
    DateTimeOffset WindowEndUtc,
    IReadOnlyList<TimeInterval> FreeSlots,
    IReadOnlyList<TimeInterval> BusyIntervals)
{
    /// <summary>
    /// True when <paramref name="candidate"/> fits entirely within some free slot (i.e. it can be
    /// booked without double-booking). The booking path's no-double-book check.
    /// </summary>
    public bool IsFree(TimeInterval candidate)
        => FreeSlots.Any(f => candidate.StartUtc >= f.StartUtc && candidate.EndUtc <= f.EndUtc);
}
