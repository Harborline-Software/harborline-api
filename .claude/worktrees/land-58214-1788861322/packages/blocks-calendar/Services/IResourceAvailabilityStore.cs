using Harborline.Api.Blocks.Calendar.Models;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.Calendar.Services;

/// <summary>
/// The store for <see cref="ResourceAvailability"/> (Slice S3) — the bookable supply per resource per
/// tenant. Keyed <c>(TenantId, ResourceRef)</c>: one availability record per resource (re-saving
/// replaces it). Tenant-scoped + cross-tenant isolated, exactly like
/// <see cref="ICalendarEventStore"/>.
/// </summary>
public interface IResourceAvailabilityStore
{
    /// <summary>Insert or replace the availability record for its <c>(TenantId, ResourceRef)</c>.</summary>
    Task SaveAsync(ResourceAvailability availability, CancellationToken ct = default);

    /// <summary>
    /// Load a resource's availability within a tenant; <see langword="null"/> if none on file (the
    /// resource has no bookable supply — free/busy yields no free slots) or it belongs to another
    /// tenant.
    /// </summary>
    Task<ResourceAvailability?> GetAsync(TenantId tenantId, ParticipantRef resourceRef, CancellationToken ct = default);

    /// <summary>List all availability records for a tenant.</summary>
    Task<IReadOnlyList<ResourceAvailability>> ListAsync(TenantId tenantId, CancellationToken ct = default);

    /// <summary>Remove a resource's availability within a tenant. Idempotent; returns true when one was removed.</summary>
    Task<bool> RemoveAsync(TenantId tenantId, ParticipantRef resourceRef, CancellationToken ct = default);
}
