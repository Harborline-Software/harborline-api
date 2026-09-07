using Harborline.Api.Blocks.Payroll.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.MultiTenancy;
using Harborline.Api.Foundation.Persistence;

namespace Harborline.Api.Blocks.Payroll.Services;

/// <summary>
/// Repository for <see cref="PayRun"/> records.
/// All methods are tenant-scoped per ADR 0092 conventions.
/// </summary>
public interface IPayRunRepository : ITenantScopedRepository<PayRun, PayRunId>
{
    /// <summary>
    /// Get a pay run by id. Returns null when missing or scoped to a
    /// different tenant (uniform-404; no diagnostic leak per ADR 0092).
    /// </summary>
    Task<PayRun?> GetAsync(
        TenantId tenantId,
        PayRunId id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Insert or update a pay run.
    /// Throws <see cref="ArgumentException"/> when <c>entity.TenantId</c>
    /// does not match <paramref name="tenantId"/>.
    /// </summary>
    Task UpsertAsync(
        TenantId tenantId,
        PayRun entity,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all pay runs for <paramref name="tenantId"/> ordered by
    /// <see cref="PayRun.PostingDate"/> descending (most recent first).
    /// </summary>
    Task<IReadOnlyList<PayRun>> ListAsync(
        TenantId tenantId,
        CancellationToken cancellationToken = default);
}
