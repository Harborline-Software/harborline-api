using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.Calendar.Models;

/// <summary>
/// A <b>subscription / scope link</b> binding a schedulable resource to a <see cref="SharedCalendar"/>
/// (Slice CALENDAR-LAYERS) — the "which shared calendars apply to this resource" edge. A doctor
/// subscribed to the clinic holiday calendar inherits all its holiday / closure exceptions; a room
/// subscribed to the same calendar inherits them too. One resource may subscribe to several shared
/// calendars (a clinic-wide holiday calendar + a department-specific closure calendar); one shared
/// calendar may have many subscribers — it is a many-to-many edge.
/// </summary>
/// <remarks>
/// <para>
/// <b>The edge, not embedded membership.</b> Decoupling the subscription from both the
/// <see cref="SharedCalendar"/> and the <see cref="ResourceAvailability"/> keeps "one entry → many
/// resources" cheap: adding a holiday touches only the shared calendar; subscribing/unsubscribing a
/// resource touches only the subscription set. The free/busy composition resolves a resource's
/// applicable shared calendars by walking its subscriptions.
/// </para>
/// <para>
/// <b>The resource is a <see cref="ParticipantRef"/></b> — exactly the booking core's
/// schedulable-resource ref (Party or Asset), so subscriptions, availability, and bookings all speak
/// the same id. Keyed <c>(TenantId, ResourceRef, SharedCalendarId)</c> in the store (a resource
/// subscribes to a given calendar at most once; re-subscribing is idempotent). Tenant-scoped +
/// cross-tenant isolated.
/// </para>
/// </remarks>
/// <param name="TenantId">The tenant this subscription belongs to (cross-tenant isolated in the store).</param>
/// <param name="ResourceRef">The schedulable resource (Party or Asset) that inherits the shared calendar's exceptions.</param>
/// <param name="SharedCalendarId">The shared calendar the resource subscribes to.</param>
public sealed record CalendarSubscription(
    TenantId TenantId,
    ParticipantRef ResourceRef,
    SharedCalendarId SharedCalendarId)
{
    /// <summary>Create a subscription of <paramref name="resourceRef"/> to <paramref name="sharedCalendarId"/>.</summary>
    public static CalendarSubscription Create(TenantId tenantId, ParticipantRef resourceRef, SharedCalendarId sharedCalendarId)
    {
        ArgumentNullException.ThrowIfNull(resourceRef);
        return new CalendarSubscription(tenantId, resourceRef, sharedCalendarId);
    }
}
