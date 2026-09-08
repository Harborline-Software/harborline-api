using Harborline.Api.Blocks.Calendar.Models;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.Calendar.Services;

/// <summary>
/// Book a demand into a free slot (Slice S3, <b>Direction A</b> — book demand INTO availability). A
/// patient into a doctor's free slot: create a <see cref="Occupancy.Bookable"/> event that consumes
/// availability, enforcing <b>no-double-book</b> on the resource. The search-and-book core of the
/// near-term schedule Module.
/// </summary>
/// <remarks>
/// <para>
/// <b>No-double-book is enforced in-block, against free/busy.</b> A booking is admitted only when its
/// slot is (a) inside an availability window and (b) free of existing occupancy — both read straight
/// off <see cref="IFreeBusyService"/> (<c>free = availability − ALL occupancy</c>). This is the
/// natural single-node guard: free/busy already computes the busy intervals, so the conflict check is
/// a containment test against the free slots. No new state, no separate lock.
/// </para>
/// <para>
/// <b>Seam to the CP reservation coordinator (noted, not wired).</b> The fleet's
/// <c>blocks-scheduling.IScheduleReservationCoordinator</c> is the <i>CP-class</i> write front-door:
/// it serializes reservation writes through a kernel Flease lease so two <i>nodes</i> cannot
/// double-book across a quorum (paper §2.2 "resource reservations → CP via distributed lease").
/// Taking a <c>ProjectReference</c> on <c>blocks-scheduling</c> here would drag the kernel-lease CP
/// machinery (and a Blazor scheduler UI) into this pure-domain block — the wrong dependency direction
/// (the S2 README already records this fence). So <see cref="IBookingService"/> enforces the
/// <i>single-node</i> no-double-book invariant locally; when a deployment needs cross-node
/// reservation serialization, a higher layer (the local-node-host / a pack) wires the booking through
/// <c>IScheduleReservationCoordinator</c> using the same UTC slot the booking produced
/// (<c>SlotReservation</c> is already <see cref="DateTimeOffset"/>-UTC, matching
/// <c>OccurrenceInstant</c>). The two compose; they are not merged here.
/// </para>
/// </remarks>
public interface IBookingService
{
    /// <summary>
    /// Book a single-occurrence <see cref="Occupancy.Bookable"/> appointment for
    /// <paramref name="resourceRef"/> in the slot [<paramref name="startUtc"/>,
    /// <paramref name="endUtc"/>), if the slot is inside the resource's availability and free of
    /// existing occupancy. On success the new event is persisted and returned; on conflict nothing is
    /// written and a <see cref="BookingOutcome.RejectionReason"/> says why.
    /// </summary>
    /// <param name="tenantId">The tenant the booking belongs to.</param>
    /// <param name="resourceRef">The resource (a doctor — Party — or a room — Asset) being booked.</param>
    /// <param name="title">The appointment title.</param>
    /// <param name="startUtc">The slot start (UTC).</param>
    /// <param name="endUtc">The slot end (UTC, exclusive) — strictly after <paramref name="startUtc"/>.</param>
    /// <param name="bookedBy">The actor making the booking (audit).</param>
    /// <param name="attendee">
    /// Optional — the demand-side party being booked in (the patient), added as an
    /// <see cref="ParticipationRole.Attendee"/>. The resource itself is always added as a
    /// <see cref="ParticipationRole.Resource"/>.
    /// </param>
    /// <param name="scheduledAgainst">Optional — the context this booking is against (the coverage seam).</param>
    /// <param name="padding">
    /// Optional — a <b>per-event padding override</b> (the padding slice). When given, it overrides the
    /// configured <see cref="IPaddingPolicy"/> default for this booking; the resulting event occupies
    /// <c>[start − Pre, end + Post]</c> for free/busy + no-double-book while the visible/booked slot
    /// stays <c>[start, end]</c>. When <see langword="null"/>, the configured policy default applies
    /// (which is <see cref="EventPadding.None"/> unless a deployment configured a non-zero default).
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<BookingOutcome> Book(
        TenantId tenantId,
        ParticipantRef resourceRef,
        string title,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        Guid bookedBy,
        ParticipantRef? attendee = null,
        ContextRef? scheduledAgainst = null,
        EventPadding? padding = null,
        CancellationToken ct = default);
}
