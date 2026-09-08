using System.Collections.Concurrent;
using Harborline.Api.Blocks.Calendar.Models;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.Calendar.Services;

/// <summary>
/// In-memory <see cref="ICalendarSubscriptionStore"/> for Slice CALENDAR-LAYERS. A subscription is a
/// pure <c>(tenant, resource-kind, resource-value, sharedCalendarId)</c> edge — no nested state to
/// serialize — so it is held as a presence set (the composite key IS the record). Idempotent
/// subscribe (re-adding the same edge is a no-op). Tenant + resource-kind are part of the key so a
/// Party id and an Asset id sharing a string value never collide, matching the
/// <see cref="InMemoryResourceAvailabilityStore"/> keying discipline.
/// </summary>
public sealed class InMemoryCalendarSubscriptionStore : ICalendarSubscriptionStore
{
    // The edge key — its presence in the dictionary IS the subscription (value is unused).
    private readonly ConcurrentDictionary<(string Tenant, ParticipantKind Kind, string Resource, Guid Calendar), byte> _edges = new();

    private static (string, ParticipantKind, string, Guid) KeyFor(TenantId tenant, ParticipantRef resource, SharedCalendarId calendar)
        => (tenant.Value, resource.Kind, resource.Value, calendar.Value);

    /// <inheritdoc />
    public Task SubscribeAsync(CalendarSubscription subscription, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        _edges[KeyFor(subscription.TenantId, subscription.ResourceRef, subscription.SharedCalendarId)] = 0;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> UnsubscribeAsync(TenantId tenantId, ParticipantRef resourceRef, SharedCalendarId sharedCalendarId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(resourceRef);
        return Task.FromResult(_edges.TryRemove(KeyFor(tenantId, resourceRef, sharedCalendarId), out _));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SharedCalendarId>> ListForResourceAsync(TenantId tenantId, ParticipantRef resourceRef, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(resourceRef);
        var tenant = tenantId.Value;
        var ids = _edges.Keys
            .Where(k => k.Tenant == tenant && k.Kind == resourceRef.Kind && k.Resource == resourceRef.Value)
            .Select(k => new SharedCalendarId(k.Calendar))
            .OrderBy(id => id.Value)
            .ToList();
        return Task.FromResult<IReadOnlyList<SharedCalendarId>>(ids);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ParticipantRef>> ListSubscribersAsync(TenantId tenantId, SharedCalendarId sharedCalendarId, CancellationToken ct = default)
    {
        var tenant = tenantId.Value;
        var subscribers = _edges.Keys
            .Where(k => k.Tenant == tenant && k.Calendar == sharedCalendarId.Value)
            .Select(k => k.Kind == ParticipantKind.Party
                ? ParticipantRef.Party(k.Resource)
                : ParticipantRef.Asset(k.Resource))
            .OrderBy(r => r.Kind)
            .ThenBy(r => r.Value, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult<IReadOnlyList<ParticipantRef>>(subscribers);
    }
}
