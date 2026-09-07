using Harborline.Api.Blocks.Payroll.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.MultiTenancy;
using Harborline.Api.Foundation.Persistence;

namespace Harborline.Api.Blocks.Payroll.Services;

/// <summary>
/// Repository for <see cref="FilingObligation"/> records.
/// All methods are tenant-scoped per ADR 0092 conventions.
/// </summary>
public interface IFilingObligationRepository : ITenantScopedRepository<FilingObligation, FilingObligationId>
{
    /// <summary>
    /// Get a filing obligation by id. Returns null when missing or scoped to a
    /// different tenant (uniform-404; no diagnostic leak per ADR 0092).
    /// </summary>
    Task<FilingObligation?> GetAsync(
        TenantId tenantId,
        FilingObligationId id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Insert or update a filing obligation.
    /// Throws <see cref="ArgumentException"/> when <c>entity.TenantId</c>
    /// does not match <paramref name="tenantId"/>.
    /// </summary>
    Task UpsertAsync(
        TenantId tenantId,
        FilingObligation entity,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns obligations for <paramref name="tenantId"/> with due dates in
    /// [<paramref name="from"/>, <paramref name="to"/>] inclusive, ordered by due date ascending.
    /// </summary>
    Task<IReadOnlyList<FilingObligation>> ListByDueDateAsync(
        TenantId tenantId,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all outstanding (not yet complete) obligations for
    /// <paramref name="tenantId"/>, ordered by due date ascending.
    /// </summary>
    Task<IReadOnlyList<FilingObligation>> ListOutstandingAsync(
        TenantId tenantId,
        CancellationToken cancellationToken = default);
}
