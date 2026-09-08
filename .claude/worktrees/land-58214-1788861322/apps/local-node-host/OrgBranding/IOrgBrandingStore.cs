using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.LocalNodeHost.OrgBranding;

/// <summary>
/// The durable store for <see cref="OrgBrandingProfile"/> — one profile per tenant, tenant-scoped and
/// cross-tenant isolated (tenant-branding slice T1). The node-local EF implementation
/// (<c>NodeEfOrgBrandingStore</c>) persists into the same SQLCipher-encrypted local-node store as the other
/// node-exclusive doctypes.
/// </summary>
public interface IOrgBrandingStore
{
    /// <summary>Returns the tenant's branding profile, or null when none is configured.</summary>
    Task<OrgBrandingProfile?> GetAsync(TenantId tenant, CancellationToken ct = default);

    /// <summary>Inserts or replaces the tenant's branding profile (one row per tenant, upsert semantics).</summary>
    Task UpsertAsync(OrgBrandingProfile profile, CancellationToken ct = default);
}
