namespace Harborline.Api.Blocks.Calendar.Models;

/// <summary>
/// A <b>viewer-scoped</b> free/busy result for one resource over a UTC window (Slice CALENDAR-LAYERS)
/// — the free slots (identical to <see cref="FreeBusyResult.FreeSlots"/>, visibility-independent) plus
/// the busy intervals as <see cref="BusyEvent"/>s whose detail is <i>redacted for events the viewing
/// principal is not authorized to see</i> (busy-to-others, detail-to-owner). The "show me this
/// resource's calendar AS principal P" query: P sees every busy slot (so it cannot double-book) but
/// only the detail of the events P is allowed to see.
/// </summary>
/// <remarks>
/// <b>Visibility never changes free/busy availability.</b> A private appointment still occupies the
/// resource and still blocks a competing booking — so <see cref="FreeSlots"/> is exactly the
/// availability-minus-all-occupancy free set, independent of the viewer. Only the <i>detail</i> of the
/// busy events is viewer-scoped. This keeps the booking-correctness invariant (a viewer who cannot see
/// a private appointment's detail still cannot book over it) intact.
/// </remarks>
/// <param name="ResourceRef">The resource (Party or Asset) this free/busy was computed for.</param>
/// <param name="ViewerActorId">The viewing principal (<see langword="null"/> = anonymous); detail is redacted for events this principal is not authorized for.</param>
/// <param name="WindowStartUtc">The query window start (UTC).</param>
/// <param name="WindowEndUtc">The query window end (UTC).</param>
/// <param name="FreeSlots">The bookable free intervals — visibility-independent (private appointments still block), clipped to the window, ordered by start.</param>
/// <param name="BusyEvents">The busy occurrences with viewer-scoped detail (redacted where the viewer is not authorized), ordered by start.</param>
public sealed record ViewerFreeBusyResult(
    ParticipantRef ResourceRef,
    Guid? ViewerActorId,
    DateTimeOffset WindowStartUtc,
    DateTimeOffset WindowEndUtc,
    IReadOnlyList<TimeInterval> FreeSlots,
    IReadOnlyList<BusyEvent> BusyEvents);
