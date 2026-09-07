using Harborline.Api.Blocks.Properties.Models;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.Properties.Services;

/// <summary>
/// Domain repository for <see cref="PropertyUnit"/>. Tenant-scoping is
/// mandatory on every call — the repository never returns units from other
/// tenants.
/// </summary>
public interface IPropertyUnitRepository
{
    /// <summary>
    /// Returns the unit with the given <paramref name="id"/>, or
    /// <c>null</c> if not found in the tenant's scope.
    /// </summary>
    Task<PropertyUnit?> GetByIdAsync(
        TenantId tenant, EntityId id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists units belonging to the given property. By default excludes
    /// archived (<see cref="PropertyUnit.ArchivedAt"/> non-null) units; pass
    /// <paramref name="includeArchived"/> to include them.
    /// </summary>
    Task<IReadOnlyList<PropertyUnit>> ListByPropertyAsync(
        TenantId tenant, PropertyId propertyId, bool includeArchived = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists units for the tenant across all properties. By default excludes
    /// archived (<see cref="PropertyUnit.ArchivedAt"/> non-null) units; pass
    /// <paramref name="includeArchived"/> to include them.
    /// </summary>
    Task<IReadOnlyList<PropertyUnit>> ListByTenantAsync(
        TenantId tenant, bool includeArchived = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts or updates the unit. Asserts that
    /// <see cref="PropertyUnit.TenantId"/> matches the caller's expected scope.
    /// </summary>
    Task UpsertAsync(PropertyUnit unit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Archives the unit by stamping <see cref="PropertyUnit.ArchivedAt"/>.
    /// The record remains queryable via the list methods with
    /// <c>includeArchived: true</c>, and can be recovered by clearing the
    /// timestamp. No-op if the unit is unknown to the tenant.
    /// </summary>
    Task ArchiveAsync(TenantId tenant, EntityId id, DateTimeOffset archivedAt, CancellationToken cancellationToken = default);
}
