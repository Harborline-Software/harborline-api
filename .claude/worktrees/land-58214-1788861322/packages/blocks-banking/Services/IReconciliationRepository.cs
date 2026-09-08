using Harborline.Api.Blocks.Banking.Models;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.Banking.Services;

/// <summary>
/// Reverse-not-delete repository for <see cref="Reconciliation"/> aggregates.
/// Per ADR 0092 tenant-keyed seam + ADR 0112 Part 1 §6.
/// </summary>
/// <remarks>
/// <para>
/// Reconciliations are <strong>reverse-not-delete</strong>. Un-reconciling a locked
/// reconciliation is an explicit reversing action with provenance (via
/// <see cref="UpdateAsync"/>); there is no <c>DeleteAsync</c>.
/// </para>
/// <para>
/// <strong>Bank-rec lock is INDEPENDENT of fiscal-period status</strong>
/// (ADR 0112 fin-acct C1). This repository does not consult
/// <c>IPeriodCloseService</c> — the match-acceptance service layer is responsible
/// for period-state gating (SoftClosed / Locked checks).
/// </para>
/// </remarks>
public interface IReconciliationRepository
{
    /// <summary>Returns a reconciliation by id (tenant-scoped), or null if not found.</summary>
    Task<Reconciliation?> GetByIdAsync(TenantId tenantId, ReconciliationId id, CancellationToken ct = default);

    /// <summary>
    /// Returns the reconciliation for a given account + period (tenant-scoped), or null if none started.
    /// One reconciliation aggregate per (account, period) pair.
    /// </summary>
    Task<Reconciliation?> GetByAccountPeriodAsync(
        TenantId tenantId, BankAccountId accountId, FiscalPeriodId periodId, CancellationToken ct = default);

    /// <summary>Returns all reconciliations for a given account (tenant-scoped).</summary>
    Task<IReadOnlyList<Reconciliation>> ListByAccountAsync(
        TenantId tenantId, BankAccountId accountId, CancellationToken ct = default);

    /// <summary>Persists a new reconciliation aggregate.</summary>
    Task AddAsync(Reconciliation reconciliation, CancellationToken ct = default);

    /// <summary>
    /// Updates a reconciliation aggregate (balance updates, lock/unlock transitions).
    /// The caller must bump <see cref="Reconciliation.Version"/> by exactly one.
    /// Returns <c>false</c> when another writer won the compare-and-swap race.
    /// Never deletes reconciliations; callers use explicit reversing actions.
    /// </summary>
    Task<bool> UpdateAsync(Reconciliation reconciliation, CancellationToken ct = default);
}
