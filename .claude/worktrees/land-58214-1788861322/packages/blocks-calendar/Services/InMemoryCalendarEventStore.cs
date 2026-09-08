using System.Collections.Concurrent;
using System.Text.Json;
using Harborline.Api.Blocks.Calendar.Models;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.Calendar.Services;

/// <summary>
/// In-memory <see cref="ICalendarEventStore"/> for Slice S0. Persists each event as a serialized
/// <see cref="CalendarEventSnapshot"/> keyed by composite <c>(TenantId, Id)</c> — so the
/// EXDATE set + RECURRENCE-ID overrides genuinely round-trip through (de)serialization, not just
/// a held object reference. A durable EF-backed store reuses the same snapshot shape later.
/// </summary>
/// <remarks>
/// Storing the JSON (rather than the live entity) is deliberate: it exercises the real persistence
/// path in tests — a saved series re-loads with its occurrence-level edits intact — and matches the
/// fleet financial-master in-memory pattern (composite <c>(TenantId, Id)</c> keying).
/// </remarks>
public sealed class InMemoryCalendarEventStore : ICalendarEventStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General);

    // Composite key (tenant, id) → serialized snapshot. ConcurrentDictionary for thread-safety
    // parity with the other in-memory fleet stores.
    private readonly ConcurrentDictionary<(string Tenant, Guid Id), string> _store = new();

    /// <inheritdoc />
    public Task SaveAsync(CalendarEvent calendarEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);
        var snapshot = CalendarEventSnapshot.FromEntity(calendarEvent);
        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        _store[(calendarEvent.TenantId.Value, calendarEvent.Id.Value)] = json;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<CalendarEvent?> GetAsync(TenantId tenantId, CalendarEventId id, CancellationToken ct = default)
    {
        if (_store.TryGetValue((tenantId.Value, id.Value), out var json))
        {
            var snapshot = JsonSerializer.Deserialize<CalendarEventSnapshot>(json, JsonOptions)
                ?? throw new InvalidOperationException($"Corrupt snapshot for calendar event {id}.");
            return Task.FromResult<CalendarEvent?>(snapshot.ToEntity());
        }
        return Task.FromResult<CalendarEvent?>(null);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<CalendarEvent>> ListAsync(TenantId tenantId, CancellationToken ct = default)
    {
        var tenant = tenantId.Value;
        var events = _store
            .Where(kvp => kvp.Key.Tenant == tenant)
            .Select(kvp => JsonSerializer.Deserialize<CalendarEventSnapshot>(kvp.Value, JsonOptions)!.ToEntity())
            .OrderBy(e => e.Start)
            .ThenBy(e => e.Id.Value)
            .ToList();
        return Task.FromResult<IReadOnlyList<CalendarEvent>>(events);
    }

    /// <inheritdoc />
    public Task<bool> RemoveAsync(TenantId tenantId, CalendarEventId id, CancellationToken ct = default)
        => Task.FromResult(_store.TryRemove((tenantId.Value, id.Value), out _));
}
