using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Foundation.MultiTenancy;

/// <summary>
/// Write seam for tenant lifecycle transitions. Deliberately separate from the
/// read-only <see cref="ITenantCatalog"/>: the catalog enumerates and resolves,
/// this service mutates lifecycle state. Tenant archived state is status-derived
/// (<see cref="TenantStatus.Archived"/>) — there is no stored archive timestamp on
/// <see cref="TenantMetadata"/>, so the marker lives in the status rather than on
/// the record.
/// </summary>
public interface ITenantLifecycleService
{
    /// <summary>
    /// Drives the tenant to <see cref="TenantStatus.Archived"/>. Idempotent: a
    /// tenant already archived stays archived. Returns the updated metadata, or
    /// <c>null</c> if no tenant with that id is registered.
    /// </summary>
    ValueTask<TenantMetadata?> ArchiveTenantAsync(
        TenantId id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Drives the tenant back to <see cref="TenantStatus.Active"/>. Archive is
    /// recoverable, so restore is the constitutive inverse of archive. Idempotent:
    /// an already-active tenant stays active. Returns the updated metadata, or
    /// <c>null</c> if no tenant with that id is registered.
    /// </summary>
    ValueTask<TenantMetadata?> RestoreTenantAsync(
        TenantId id,
        CancellationToken cancellationToken = default);
}
