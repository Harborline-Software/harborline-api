using System.Collections.Concurrent;
using Harborline.Api.Blocks.Properties.Models;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.Properties.Services;

/// <summary>
/// In-memory <see cref="IPropertyUnitRepository"/> for development,
/// testing, and kitchen-sink demos. Replace with a persistence-backed
/// implementation in production hosts.
/// </summary>
public sealed class InMemoryPropertyUnitRepository : IPropertyUnitRepository
{
    private readonly ConcurrentDictionary<(TenantId, EntityId), PropertyUnit> _store = new();

    /// <inheritdoc />
    public Task<PropertyUnit?> GetByIdAsync(
        TenantId tenant, EntityId id, CancellationToken cancellationToken = default)
    {
        _store.TryGetValue((tenant, id), out var unit);
        return Task.FromResult(unit);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<PropertyUnit>> ListByPropertyAsync(
        TenantId tenant, PropertyId propertyId, bool includeArchived = false, CancellationToken cancellationToken = default)
    {
        var query = _store
            .Where(kvp => kvp.Key.Item1.Equals(tenant)
                       && kvp.Value.PropertyId.Equals(propertyId))
            .Select(kvp => kvp.Value);

        if (!includeArchived)
        {
            query = query.Where(u => u.ArchivedAt is null);
        }

        IReadOnlyList<PropertyUnit> result = query.ToList();
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<PropertyUnit>> ListByTenantAsync(
        TenantId tenant, bool includeArchived = false, CancellationToken cancellationToken = default)
    {
        var query = _store
            .Where(kvp => kvp.Key.Item1.Equals(tenant))
            .Select(kvp => kvp.Value);

        if (!includeArchived)
        {
            query = query.Where(u => u.ArchivedAt is null);
        }

        IReadOnlyList<PropertyUnit> result = query.ToList();
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task UpsertAsync(PropertyUnit unit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(unit);
        _store[(unit.TenantId, unit.Id)] = unit;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ArchiveAsync(
        TenantId tenant, EntityId id, DateTimeOffset archivedAt, CancellationToken cancellationToken = default)
    {
        if (_store.TryGetValue((tenant, id), out var existing))
        {
            _store[(tenant, id)] = existing with { ArchivedAt = archivedAt };
        }

        return Task.CompletedTask;
    }
}
