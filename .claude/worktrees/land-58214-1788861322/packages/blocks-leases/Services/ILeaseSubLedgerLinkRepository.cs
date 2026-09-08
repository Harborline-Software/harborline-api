using Harborline.Api.Blocks.Leases.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.MultiTenancy;

namespace Harborline.Api.Blocks.Leases.Services;

/// <summary>
/// Storage interface for <see cref="LeaseSubLedgerLink"/> records (ADR 0120 PR-D).
///
/// <para>
/// <b>Tenant-keying posture (ADR 0092):</b> every method takes <see cref="TenantId"/>
/// as the first positional parameter. Read methods filter by tenant and return null /
/// empty on cross-tenant (uniform-404 invariant — no diagnostic leak).
/// </para>
///
/// <para>
/// <b>Composite keying:</b> the link is keyed on <c>(TenantId, LeaseId)</c> — one
/// active link per lease per tenant in v1 (per-lease granularity).
/// </para>
/// </summary>
public interface ILeaseSubLedgerLinkRepository
{
    /// <summary>
    /// Insert or update the <paramref name="link"/>. Idempotent: calling
    /// with the same link twice (e.g. from ERPNext import re-runs) produces no side-effect
    /// beyond updating the existing record.
    /// </summary>
    Task UpsertAsync(
        TenantId tenantId,
        LeaseSubLedgerLink link,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get the sub-ledger link for <paramref name="leaseId"/>. Returns null when no link
    /// has been recorded yet (lease not yet activated) OR when the lease belongs to a
    /// different tenant (uniform-404).
    /// </summary>
    Task<LeaseSubLedgerLink?> GetByLeaseAsync(
        TenantId tenantId,
        LeaseId leaseId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// List all links in <paramref name="tenantId"/>. Used by migration sweeps.
    /// </summary>
    Task<IReadOnlyList<LeaseSubLedgerLink>> ListAllAsync(
        TenantId tenantId,
        CancellationToken cancellationToken = default);
}
