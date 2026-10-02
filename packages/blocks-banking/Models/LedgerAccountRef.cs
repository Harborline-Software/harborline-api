using Harborline.Api.Blocks.FinancialLedger.Models;

namespace Harborline.Api.Blocks.Banking.Models;

/// <summary>
/// A reference to the GL account that a <see cref="BankAccount"/> reconciles against.
/// Per ADR 0112 Part 1 §1 — the cash account in the general ledger.
/// </summary>
/// <param name="GLAccountId">The GL account identifier.</param>
/// <param name="ChartId">
/// The chart-of-accounts the GL account belongs to, or <see langword="null"/> when none is known. Optional for the
/// same reason <see cref="GLAccount.ChartId"/> is: an account created on the local node names no chart (T-1049).
/// </param>
public readonly record struct LedgerAccountRef(
    GLAccountId GLAccountId,
    ChartOfAccountsId? ChartId);
