using System.Collections.Concurrent;
using System.Text.Json;
using Harborline.Api.Blocks.Calendar.Models;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.Calendar.Services;

/// <summary>
/// In-memory <see cref="ISharedCalendarStore"/> for Slice CALENDAR-LAYERS. Persists each shared
/// calendar as a serialized <see cref="SharedCalendarSnapshot"/> keyed by composite
/// <c>(TenantId, SharedCalendarId)</c> — so the exception spans genuinely round-trip through
/// (de)serialization, matching the <see cref="InMemoryCalendarEventStore"/> /
/// <see cref="InMemoryResourceAvailabilityStore"/> pattern. A durable EF-backed store reuses the same
/// snapshot shape later.
/// </summary>
public sealed class InMemorySharedCalendarStore : ISharedCalendarStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General);

    private readonly ConcurrentDictionary<(string Tenant, Guid Id), string> _store = new();

    private static (string, Guid) KeyFor(TenantId tenant, SharedCalendarId id) => (tenant.Value, id.Value);

    /// <inheritdoc />
    public Task SaveAsync(SharedCalendar calendar, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        var json = JsonSerializer.Serialize(SharedCalendarSnapshot.FromEntity(calendar), JsonOptions);
        _store[KeyFor(calendar.TenantId, calendar.Id)] = json;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<SharedCalendar?> GetAsync(TenantId tenantId, SharedCalendarId id, CancellationToken ct = default)
    {
        if (_store.TryGetValue(KeyFor(tenantId, id), out var json))
        {
            var snapshot = JsonSerializer.Deserialize<SharedCalendarSnapshot>(json, JsonOptions)
                ?? throw new InvalidOperationException($"Corrupt shared-calendar snapshot for {id}.");
            return Task.FromResult<SharedCalendar?>(snapshot.ToEntity());
        }
        return Task.FromResult<SharedCalendar?>(null);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SharedCalendar>> ListAsync(TenantId tenantId, CancellationToken ct = default)
    {
        var tenant = tenantId.Value;
        var records = _store
            .Where(kvp => kvp.Key.Tenant == tenant)
            .Select(kvp => JsonSerializer.Deserialize<SharedCalendarSnapshot>(kvp.Value, JsonOptions)!.ToEntity())
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .ThenBy(c => c.Id.Value)
            .ToList();
        return Task.FromResult<IReadOnlyList<SharedCalendar>>(records);
    }

    /// <inheritdoc />
    public Task<bool> RemoveAsync(TenantId tenantId, SharedCalendarId id, CancellationToken ct = default)
        => Task.FromResult(_store.TryRemove(KeyFor(tenantId, id), out _));
}
