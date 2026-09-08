using Harborline.Api.Blocks.Calendar.Models;

namespace Harborline.Api.Blocks.Calendar.Services;

/// <summary>
/// The outcome of an <see cref="IBookingService.Book"/> call (Slice S3, Direction A). Mirrors the
/// shape of <c>blocks-scheduling</c>'s <c>ReservationOutcome</c> / <c>Kernel.Ledger.PostingResult</c>
/// so the booking path speaks the same success/rejection vocabulary as the fleet's other write
/// front-doors.
/// </summary>
/// <param name="Success">
/// <see langword="true"/> iff the booking was created. When <see langword="false"/>,
/// <see cref="Event"/> is <see langword="null"/> and <see cref="RejectionReason"/> says why.
/// </param>
/// <param name="Event">The created <see cref="Occupancy.Bookable"/> event on success; <see langword="null"/> on rejection.</param>
/// <param name="RejectionReason">
/// Non-null iff <see cref="Success"/> is false. Canonical values:
/// <list type="bullet">
///   <item><description><c>NO_AVAILABILITY</c> — the requested slot is not inside any of the
///   resource's availability windows (nothing to book into).</description></item>
///   <item><description><c>SLOT_CONFLICT</c> — the slot overlaps existing occupancy on the resource
///   (the no-double-book guard). The competing time is busy.</description></item>
///   <item><description><c>SLOT_INVERTED</c> — the requested end is at or before the start.</description></item>
/// </list>
/// </param>
public sealed record BookingOutcome(
    bool Success,
    CalendarEvent? Event,
    string? RejectionReason = null)
{
    /// <summary>Canonical rejection reason: the slot is outside the resource's availability windows.</summary>
    public const string NoAvailability = "NO_AVAILABILITY";

    /// <summary>Canonical rejection reason: the slot overlaps existing occupancy (no-double-book).</summary>
    public const string SlotConflict = "SLOT_CONFLICT";

    /// <summary>Canonical rejection reason: the requested end is at or before the start.</summary>
    public const string SlotInverted = "SLOT_INVERTED";

    internal static BookingOutcome Booked(CalendarEvent ev) => new(true, ev);
    internal static BookingOutcome Rejected(string reason) => new(false, null, reason);
}
