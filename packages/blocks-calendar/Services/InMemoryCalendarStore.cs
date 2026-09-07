using System.Collections.Concurrent;
using System.Text.Json;

using Harborline.Api.Blocks.Calendar.Models;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.Calendar.Services;

/// <summary>
/// In-memory <see cref="ICalendarStore"/> for the block default (calendar-productization design note
/// #149, slice C1). Persists each calendar as a serialized <see cref="CalendarSnapshot"/> keyed by the
/// composite <c>(TenantId, Id)</c> — so it exercises the REAL (de)serialization round-trip in tests, and
/// the durable EF-backed node store reuses the same snapshot shape. Mirrors
/// <see cref="InMemoryCalendarEventStore"/>.
/// </summary>
public sealed class InMemoryCalendarStore : ICalendarStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General);

    private readonly ConcurrentDictionary<(string Tenant, Guid Id), string> _store = new();

    /// <inheritdoc />
    public Task SaveAsync(OwnedCalendar calendar, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        var json = JsonSerializer.Serialize(CalendarSnapshot.FromEntity(calendar), JsonOptions);
        _store[(calendar.TenantId.Value, calendar.Id.Value)] = json;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<OwnedCalendar?> GetAsync(TenantId tenantId, CalendarId id, CancellationToken ct = default)
    {
        if (_store.TryGetValue((tenantId.Value, id.Value), out var json))
        {
            return Task.FromResult<OwnedCalendar?>(Deserialize(json).ToEntity());
        }
        return Task.FromResult<OwnedCalendar?>(null);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<OwnedCalendar>> ListAsync(TenantId tenantId, CancellationToken ct = default)
    {
        var tenant = tenantId.Value;
        var calendars = _store
            .Where(kvp => kvp.Key.Tenant == tenant)
            .Select(kvp => Deserialize(kvp.Value).ToEntity())
            .OrderByDescending(c => c.IsDefault)   // the default calendar sorts first
            .ThenBy(c => c.CreatedAt)
            .ThenBy(c => c.Id.Value)
            .ToList();
        return Task.FromResult<IReadOnlyList<OwnedCalendar>>(calendars);
    }

    /// <inheritdoc />
    public Task<bool> RemoveAsync(TenantId tenantId, CalendarId id, CancellationToken ct = default)
        => Task.FromResult(_store.TryRemove((tenantId.Value, id.Value), out _));

    private static CalendarSnapshot Deserialize(string json)
        => JsonSerializer.Deserialize<CalendarSnapshot>(json, JsonOptions)
           ?? throw new InvalidOperationException("Corrupt calendar snapshot in the in-memory store.");
}
