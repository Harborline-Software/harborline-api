namespace Harborline.Api.Blocks.Banking.Models;

/// <summary>
/// Lock state for a <see cref="Reconciliation"/> aggregate (bank-reconciliation lock).
/// Per ADR 0112 fin-acct C1 — this is INDEPENDENT of <c>FiscalPeriodStatus</c>.
/// The bank-rec lock and the fiscal-period lock are two distinct controls.
/// </summary>
/// <remarks>
/// <para>
/// A bank-reconciliation lock prevents silent re-matching into an already-reconciled
/// period. Un-reconciling a locked reconciliation is an explicit, provenance-bearing
/// reversing action (reverse-not-delete).
/// </para>
/// <para>
/// This enum governs <see cref="Reconciliation.LockState"/> and MUST NOT be
/// conflated with <c>FiscalPeriodStatus</c> from <c>blocks-financial-periods</c>.
/// Reconciling a bank account for June does NOT lock the June fiscal period.
/// </para>
/// </remarks>
public enum BankReconciliationLockState
{
    /// <summary>Reconciliation in progress; lines can be matched or un-matched.</summary>
    Open,

    /// <summary>
    /// Reconciliation completed and locked. Re-matching into this reconciliation
    /// requires an explicit unlock-with-provenance action.
    /// Un-locking is reverse-not-delete; it records who unlocked and when.
    /// </summary>
    Locked,
}
