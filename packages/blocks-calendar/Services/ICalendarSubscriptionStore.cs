using Harborline.Api.Blocks.Calendar.Models;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.Calendar.Services;

/// <summary>
/// The store for <see cref="CalendarSubscription"/> (Slice CALENDAR-LAYERS) — the resource↔shared-
/// calendar scope links ("which shared calendars apply to this resource"). A many-to-many edge:
/// keyed <c>(TenantId, ResourceRef, SharedCalendarId)</c> (a resource subscribes to a given calendar
/// at most once — re-subscribing is idempotent). The free/busy composition resolves a resource's
/// applicable shared calendars by querying <see cref="ListForResourceAsync"/>. Tenant-scoped +
/// cross-tenant isolated.
/// </summary>
public interface ICalendarSubscriptionStore
{
    /// <summary>Subscribe a resource to a shared calendar. Idempotent (re-subscribing is a no-op).</summary>
    Task SubscribeAsync(CalendarSubscription subscription, CancellationToken ct = default);

    /// <summary>
    /// Unsubscribe a resource from a shared calendar. Idempotent; returns true when a subscription was
    /// removed.
    /// </summary>
    Task<bool> UnsubscribeAsync(TenantId tenantId, ParticipantRef resourceRef, SharedCalendarId sharedCalendarId, CancellationToken ct = default);

    /// <summary>
    /// The shared-calendar ids a resource subscribes to within a tenant — the "which shared calendars
    /// apply to this resource" answer the free/busy composition needs.
    /// </summary>
    Task<IReadOnlyList<SharedCalendarId>> ListForResourceAsync(TenantId tenantId, ParticipantRef resourceRef, CancellationToken ct = default);

    /// <summary>The resources subscribed to a given shared calendar within a tenant (the inverse query).</summary>
    Task<IReadOnlyList<ParticipantRef>> ListSubscribersAsync(TenantId tenantId, SharedCalendarId sharedCalendarId, CancellationToken ct = default);
}
