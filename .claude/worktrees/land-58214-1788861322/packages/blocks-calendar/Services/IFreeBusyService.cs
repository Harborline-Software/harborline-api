using Harborline.Api.Blocks.Calendar.Models;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.Calendar.Services;

/// <summary>
/// The free/busy query (Slice S3) — THE query that makes booking and coverage work.
/// <c>free = availability windows − ALL occupancy (Bookable + Blocking + Tentative events on the
/// resource)</c>. A doctor's <see cref="Occupancy.Blocking"/> lunch removes the slot; a booked
/// <see cref="Occupancy.Bookable"/> appointment consumes availability; the remaining gaps are the
/// free slots a booking can land in.
/// </summary>
/// <remarks>
/// <para>
/// <b>Direction A (booking) substrate, coverage-ready.</b> Free/busy is the bookable surface for the
/// near-term booking model. It is computed from the <i>classified</i> event stream (the
/// <see cref="Occupancy"/> flag) — the same clean classified record a future coverage overlay reads
/// by context. The core never computes coverage; it exposes free/busy + by-context, the two
/// generic operations both booking and a coverage overlay build on.
/// </para>
/// <para>
/// <b>Occupancy = which events make the resource busy.</b> Every non-cancelled event where the
/// resource appears (as the <see cref="CalendarEvent.ResourceRef"/> or any
/// <see cref="CalendarParticipation"/>) contributes its occurrence instants as busy time, regardless
/// of occupancy class — Bookable, Blocking, <i>and</i> Tentative all block a competing booking. The
/// occupancy <i>class</i> is carried so a vertical overlay can distinguish billable from blocking;
/// free/busy itself only cares that the time is occupied.
/// </para>
/// </remarks>
public interface IFreeBusyService
{
    /// <summary>
    /// Compute free/busy for <paramref name="resourceRef"/> over the UTC window — the free (bookable)
    /// slots and the busy intervals. Tenant-scoped + cross-tenant isolated (only this tenant's
    /// availability + events are considered). A resource with no availability record yields no free
    /// slots (availability is the supply that must exist first).
    /// </summary>
    Task<FreeBusyResult> FreeBusy(
        TenantId tenantId,
        ParticipantRef resourceRef,
        DateTimeOffset windowStartUtc,
        DateTimeOffset windowEndUtc,
        CancellationToken ct = default);

    /// <summary>
    /// The merged <b>occupied</b> intervals on <paramref name="resourceRef"/> overlapping the UTC
    /// window — every non-cancelled event's <b>occupied footprint</b> (visible span extended by the
    /// event's padding), regardless of availability. This is the raw no-double-book surface: unlike
    /// <see cref="FreeBusyResult.BusyIntervals"/> (which reports only occupancy <i>inside</i>
    /// availability, for display), this reports occupancy that may spill <i>past</i> an availability
    /// edge — a doctor's post-appointment documentation padding running past closing time still blocks
    /// a back-to-back booking. The booking path's conflict test reads this so a candidate's occupied
    /// footprint is rejected when it overlaps existing occupancy even outside availability.
    /// </summary>
    /// <returns>The merged, ordered occupied intervals overlapping the window (empty when the resource is free).</returns>
    Task<IReadOnlyList<TimeInterval>> OccupiedIntervals(
        TenantId tenantId,
        ParticipantRef resourceRef,
        DateTimeOffset windowStartUtc,
        DateTimeOffset windowEndUtc,
        CancellationToken ct = default);

    /// <summary>
    /// Compute free/busy for <paramref name="resourceRef"/> <b>as seen by</b>
    /// <paramref name="viewerActorId"/> (Slice CALENDAR-LAYERS) — the same free slots (visibility never
    /// changes availability: a private appointment still blocks a booking), but the busy events carry
    /// <i>viewer-scoped detail</i>: a <see cref="EventVisibility.Private"/> appointment shows as BUSY
    /// with its title redacted unless the viewer is authorized (the owner, or — via a host-registered
    /// grant-backed <see cref="IEventDetailVisibilityPolicy"/> — a granted principal). Busy-to-others,
    /// detail-to-owner. Pass <paramref name="viewerActorId"/> = <see langword="null"/> for an anonymous
    /// viewer (every private appointment is redacted).
    /// </summary>
    Task<ViewerFreeBusyResult> FreeBusyForViewer(
        TenantId tenantId,
        ParticipantRef resourceRef,
        Guid? viewerActorId,
        DateTimeOffset windowStartUtc,
        DateTimeOffset windowEndUtc,
        CancellationToken ct = default);
}
