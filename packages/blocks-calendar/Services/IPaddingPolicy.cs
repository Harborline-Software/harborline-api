using Harborline.Api.Blocks.Calendar.Models;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.Calendar.Services;

/// <summary>
/// Resolves the <b>default</b> <see cref="EventPadding"/> for a booking — the configured pre/post
/// time-footprint to apply when an event is created via the booking path (the padding slice). The
/// MECHANISM (pre/post fields + the occupied-interval math free/busy reasons about) is built into the
/// calendar core; the VALUES come from here. A policy resolves padding <i>per resource / per
/// event-type / per domain</i> ("doctor appt = 5 pre + 30 post"); the booking path applies the
/// resolved default, and any single event can still override it (<see cref="CalendarEvent.SetPadding"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Default vs. override.</b> This policy is the <i>default-at-booking</i> source. It is consulted
/// once, when <see cref="IBookingService.Book"/> creates the event, to seed the event's padding. The
/// event then carries its own <see cref="CalendarEvent.Padding"/> (the resolved default, or a later
/// per-event override) — and free/busy reads that stored value, never re-consulting the policy. So a
/// per-event override is just a different stored value, and free/busy is a pure function of the event
/// state (the same discipline as the rest of the block).
/// </para>
/// <para>
/// <b>Core gives the mechanism + a configurable default; verticals give the rich values.</b> The
/// default in-core policy (<see cref="DefaultPaddingPolicy"/>) returns one configured padding (or
/// <see cref="EventPadding.None"/>) regardless of the booking — enough for "every appointment on this
/// tenant gets 5 pre + 30 post". A vertical Pack registers a richer <see cref="IPaddingPolicy"/> that
/// looks up the padding by the resource's specialty, the event type, or the tenant's clinic config.
/// The core never enumerates those dimensions; it only consults the resolver.
/// </para>
/// </remarks>
public interface IPaddingPolicy
{
    /// <summary>
    /// Resolve the default padding for a booking of <paramref name="resourceRef"/> in
    /// <paramref name="tenantId"/>. The booking path applies the result to the new event. Implementations
    /// must return <see cref="EventPadding.None"/> when no padding applies (the backward-compatible
    /// default — an unpadded booking behaves exactly as before).
    /// </summary>
    /// <param name="tenantId">The tenant the booking belongs to.</param>
    /// <param name="resourceRef">The resource being booked (a doctor — Party — or a room — Asset).</param>
    /// <returns>The configured default padding; <see cref="EventPadding.None"/> when none applies.</returns>
    EventPadding ResolveDefault(TenantId tenantId, ParticipantRef resourceRef);
}
