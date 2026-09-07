using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialSubLedger.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.MultiTenancy;

namespace Harborline.Api.Blocks.FinancialSubLedger.Services;

/// <summary>
/// Read-model interface for subsidiary-ledger positions and history (ADR 0120).
/// The implementation lives in the HIGH-tier projection assembly
/// <c>Harborline.Api.Blocks.FinancialSubLedger.Projection</c> (PR-B), which
/// <c>ProjectReference</c>s <c>blocks-financial-ar</c>, <c>blocks-financial-ap</c>,
/// and <c>blocks-financial-payments</c>. This interface lives in the LOW identity
/// assembly so domain packs can depend on it without pulling the heavy reference closure.
///
/// <para>
/// <b>Position derivation (C-FIN-1):</b> position is
/// <c>Σ(open-item.Balance) − Σ(unapplied credits)</c>. Open-item
/// <c>Balance</c> already nets discount and write-off via <c>AmountPaid</c>
/// — derived from the open-item <c>Balance</c> field, NOT from
/// <c>Σ AmountApplied</c> of cash applications, or discounted/short-paid
/// items will show phantom residuals.
/// </para>
///
/// <para>
/// <b>R1 invariant (asserted in PR-B arch-tests, C-FIN-2):</b>
/// <c>Σ(derived positions, ControlAccountId=C, ChartId=K) == GL control
/// balance(C, K)</c>. Must hold after a void AND after a write-off (the
/// GL reversal / bad-debt JE keeps both sides balanced simultaneously).
/// </para>
/// </summary>
public interface ISubLedgerReadModel
{
    /// <summary>
    /// Returns the current derived position of <paramref name="subLedgerAccountId"/>:
    /// open-item balances minus unapplied credits.
    /// </summary>
    Task<SubLedgerPosition> GetPositionAsync(
        TenantId tenantId,
        SubLedgerAccountId subLedgerAccountId,
        DateOnly asOf,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the chronological history of open items and applications for
    /// <paramref name="subLedgerAccountId"/> — invoices/bills + payment applications
    /// + adjustments.
    /// </summary>
    Task<IReadOnlyList<SubLedgerEntry>> GetHistoryAsync(
        TenantId tenantId,
        SubLedgerAccountId subLedgerAccountId,
        DateTimeOffset asOf,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the aging summary scoped to <paramref name="subLedgerAccountId"/>.
    /// This is a new scope on the existing <c>IArAgingService</c> / <c>IApAgingService</c>
    /// per-sub-ledger granularity (ADR 0120 §4) — implemented in PR-B.
    /// </summary>
    Task<AgingSummary> GetAgingForSubLedgerAsync(
        TenantId tenantId,
        SubLedgerAccountId subLedgerAccountId,
        DateOnly asOf,
        CancellationToken cancellationToken = default);
}
