using Harborline.Api.Blocks.Calendar.Models;

namespace Harborline.Api.Blocks.Calendar.Services;

/// <summary>
/// The pluggable <b>detail-visibility policy</b> (Slice CALENDAR-LAYERS) — decides whether a viewing
/// principal may see a <see cref="EventVisibility.Private"/> event's detail fields, or only its busy
/// time. The
/// calendar-layers seam that ties event-detail visibility to the fleet's existing access model
/// WITHOUT dragging the authorization substrate into this pure-domain block.
/// </summary>
/// <remarks>
/// <para>
/// <b>The grant tie, the right-sized way.</b> The schedule-feature design states visibility is "the
/// existing grant model applied to event fields, NOT a new scheduling concept". A grant decision
/// needs the heavyweight ADR 0117 substrate (<c>IPermissionResolver</c> / <c>IGrantStore</c> /
/// <c>ShipRole</c> / <c>ShipAction</c>), which lives in <c>blocks-access-grant</c> and its
/// authorization chain. Taking a <c>ProjectReference</c> on that from a pure-domain block would invert
/// the dependency direction (the same reason <see cref="ParticipantRef"/> stores id <i>values</i>
/// instead of referencing <c>blocks-assets</c>). So this block defines a thin policy <i>interface</i>
/// + a safe <see cref="OwnerOnlyEventDetailVisibilityPolicy"/> default; a host (the Bridge / a vertical
/// Pack) registers a grant-backed implementation that asks "does this principal hold a grant that
/// authorizes reading this event's detail?" — composing the existing model at the edge, exactly as the
/// design prescribes.
/// </para>
/// <para>
/// <b>Fail-closed default.</b> The shipped <see cref="OwnerOnlyEventDetailVisibilityPolicy"/> reveals
/// a <see cref="EventVisibility.Private"/> event's detail ONLY to the owner principal (the event's
/// creator/owner). It never reveals private detail to a non-owner — so an integrator who forgets to
/// register the grant-backed policy leaks no private detail (busy-to-others is the safe degradation). A
/// <see cref="EventVisibility.Public"/> event's detail is always visible (the policy is consulted only
/// for private events).
/// </para>
/// </remarks>
public interface IEventDetailVisibilityPolicy
{
    /// <summary>
    /// May <paramref name="viewerActorId"/> see the full detail of <paramref name="ev"/>? Consulted
    /// only for <see cref="EventVisibility.Private"/> events (a <see cref="EventVisibility.Public"/>
    /// event's detail is always visible). Return <see langword="true"/> to reveal the detail,
    /// <see langword="false"/> to show busy-only (redacted). A <see langword="null"/>
    /// <paramref name="viewerActorId"/> is an unauthenticated / system-anonymous viewer — the default
    /// policy treats it as not-the-owner (redact).
    /// </summary>
    bool CanSeeDetail(CalendarEvent ev, Guid? viewerActorId);
}

/// <summary>
/// The safe default <see cref="IEventDetailVisibilityPolicy"/> (Slice CALENDAR-LAYERS) — a
/// <see cref="EventVisibility.Private"/> event's detail is visible ONLY to its owner (the event's
/// <see cref="CalendarEvent.OwnerActorId"/>, or its <see cref="CalendarEvent.CreatedBy"/> when no
/// explicit owner is set). Fail-closed: a non-owner (or an anonymous viewer) sees busy-only. A host
/// replaces this with a grant-backed policy (ADR 0117) to widen visibility to authorized principals
/// (a shared-team-calendar member, a delegate) without touching this block.
/// </summary>
public sealed class OwnerOnlyEventDetailVisibilityPolicy : IEventDetailVisibilityPolicy
{
    /// <inheritdoc />
    public bool CanSeeDetail(CalendarEvent ev, Guid? viewerActorId)
    {
        ArgumentNullException.ThrowIfNull(ev);
        if (ev.Visibility == EventVisibility.Public) return true;   // public detail is always visible
        if (viewerActorId is null) return false;                    // anonymous viewer → redact
        return viewerActorId.Value == ev.OwnerActorId;              // owner-only for a private event
    }
}
