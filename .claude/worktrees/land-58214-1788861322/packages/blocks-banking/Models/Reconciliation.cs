using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.MultiTenancy;

namespace Harborline.Api.Blocks.Banking.Models;

/// <summary>
/// Per-account / per-period reconciliation aggregate.
/// Per ADR 0112 Part 1 §6 — reconciliation state per account/period.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Bank-rec lock ≠ fiscal-period lock (ADR 0112 fin-acct C1 — highest severity).</strong>
/// <see cref="LockState"/> is a bank-reconciliation lock (reconcile weekly), wholly
/// independent of <c>FiscalPeriodStatus</c> (close monthly/quarterly). Reconciling
/// a bank account for June does NOT lock the June fiscal period.
/// </para>
/// <para>
/// <strong>Reconciliation arithmetic invariant (checkable):</strong>
/// <c>OpeningBalance + ClearedMovement == StatementClosingBalance</c>.
/// Any discrepancy indicates unreconciled lines and must be surfaced to the operator.
/// </para>
/// <para>
/// <strong>Reverse-not-delete:</strong> un-reconciling a locked reconciliation is
/// an explicit reversing action with provenance, not a row deletion. The reconciliation
/// aggregate records who unlocked it and when via <see cref="LockedByPrincipalId"/>
/// and the durable audit layer.
/// </para>
/// <para>
/// <strong>ClearedMovement:</strong> sum of all cleared (Accepted <see cref="MatchLink"/>)
/// movements within this period. Must satisfy:
/// <c>OpeningBalance + ClearedMovement == StatementClosingBalance</c>
/// when the reconciliation is completed and locked.
/// </para>
/// </remarks>
public sealed record Reconciliation(
    ReconciliationId Id,
    TenantId TenantId,
    BankAccountId AccountId,
    FiscalPeriodId PeriodId,
    decimal OpeningBalance,
    decimal StatementClosingBalance,
    decimal ClearedMovement,
    BankReconciliationLockState LockState,
    Instant? LockedAt,
    string? LockedByPrincipalId,
    Instant CreatedAtUtc,
    Instant UpdatedAtUtc,
    int Version = 0) : IMustHaveTenant
{
    /// <summary>
    /// Returns <c>true</c> when the reconciliation arithmetic balances:
    /// <c>OpeningBalance + ClearedMovement == StatementClosingBalance</c>.
    /// Uses a tolerance of 0.0001 to guard against floating-point drift on
    /// multi-line sums; callers storing exact <see cref="decimal"/> arithmetic
    /// may use strict equality checks in tests.
    /// </summary>
    public bool IsBalanced =>
        Math.Abs(OpeningBalance + ClearedMovement - StatementClosingBalance) < 0.0001m;
}
