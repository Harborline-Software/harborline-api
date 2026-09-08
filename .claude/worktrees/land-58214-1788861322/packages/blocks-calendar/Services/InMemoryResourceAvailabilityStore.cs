using System.Collections.Concurrent;
using System.Text.Json;
using Harborline.Api.Blocks.Calendar.Models;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.Calendar.Services;

/// <summary>
/// In-memory <see cref="IResourceAvailabilityStore"/> for Slice S3. Persists each availability record
/// as a serialized <see cref="ResourceAvailabilitySnapshot"/> keyed by composite
/// <c>(TenantId, Kind, ResourceValue)</c> — so the windows + exception dates genuinely round-trip
/// through (de)serialization, matching the <see cref="InMemoryCalendarEventStore"/> pattern. A
/// durable EF-backed store reuses the same snapshot shape later.
/// </summary>
public sealed class InMemoryResourceAvailabilityStore : IResourceAvailabilityStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General);

    // Composite key (tenant, resource-kind, resource-value) → serialized snapshot. The kind is part
    // of the key so a Party id and an Asset id that happen to share a string value never collide.
    private readonly ConcurrentDictionary<(string Tenant, ParticipantKind Kind, string Resource), string> _store = new();

    private static (string, ParticipantKind, string) KeyFor(TenantId tenant, ParticipantRef resource)
        => (tenant.Value, resource.Kind, resource.Value);

    /// <inheritdoc />
    public Task SaveAsync(ResourceAvailability availability, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(availability);
        var snapshot = ResourceAvailabilitySnapshot.FromEntity(availability);
        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        _store[KeyFor(availability.TenantId, availability.ResourceRef)] = json;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<ResourceAvailability?> GetAsync(TenantId tenantId, ParticipantRef resourceRef, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(resourceRef);
        if (_store.TryGetValue(KeyFor(tenantId, resourceRef), out var json))
        {
            var snapshot = JsonSerializer.Deserialize<ResourceAvailabilitySnapshot>(json, JsonOptions)
                ?? throw new InvalidOperationException($"Corrupt availability snapshot for resource {resourceRef}.");
            return Task.FromResult<ResourceAvailability?>(snapshot.ToEntity());
        }
        return Task.FromResult<ResourceAvailability?>(null);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ResourceAvailability>> ListAsync(TenantId tenantId, CancellationToken ct = default)
    {
        var tenant = tenantId.Value;
        var records = _store
            .Where(kvp => kvp.Key.Tenant == tenant)
            .Select(kvp => JsonSerializer.Deserialize<ResourceAvailabilitySnapshot>(kvp.Value, JsonOptions)!.ToEntity())
            .OrderBy(r => r.ResourceRef.Kind)
            .ThenBy(r => r.ResourceRef.Value)
            .ToList();
        return Task.FromResult<IReadOnlyList<ResourceAvailability>>(records);
    }

    /// <inheritdoc />
    public Task<bool> RemoveAsync(TenantId tenantId, ParticipantRef resourceRef, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(resourceRef);
        return Task.FromResult(_store.TryRemove(KeyFor(tenantId, resourceRef), out _));
    }
}
