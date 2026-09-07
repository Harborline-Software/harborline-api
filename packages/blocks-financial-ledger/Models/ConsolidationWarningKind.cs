namespace Harborline.Api.Blocks.FinancialLedger.Models;

/// <summary>
/// Classifies a <see cref="ConsolidationWarning"/> surfaced by
/// <see cref="Services.IConsolidationService.ConsolidateAsync"/> (ADR 0105 §3.3).
/// A consolidation is rendered <b>with</b> its warnings, never blocked by them and
/// never silently corrected — warnings are real accounting signals the owner / CPA
/// should chase, and a non-tying statement must surface visibly (financial F-3).
/// </summary>
public enum ConsolidationWarningKind
{
    /// <summary>
    /// Residual (i): a matched inter-company balance-sheet pair (trade due-from ↔
    /// due-to, inter-company loan, or investment-in-sub ↔ intercompany-equity) does
    /// NOT net to zero across the in-scope counterparties — one side booked a
    /// balance the other did not. Both sides are still removed; the residual is
    /// disclosed (ADR 0105 §3.3).
    /// </summary>
    IntercompanyImbalance,

    /// <summary>
    /// Residual (i), income-statement variant: intra-group revenue and its matching
    /// intra-group expense were recognized in <b>different periods</b>, so the
    /// eliminated amounts do not net to zero. Naive netting would distort group net
    /// income, so the mismatch is surfaced rather than silently netted
    /// (ADR 0105 §3.3, financial F-6).
    /// </summary>
    IncomeStatementTimingMismatch,

    /// <summary>
    /// Residual (ii): the overall consolidated balance sheet does not tie
    /// (Σ of signed balances ≠ 0 after elimination). Surfaced just as loudly as (i)
    /// and <b>NEVER</b> forced to zero by a balancing plug — a quietly-balanced
    /// non-tying statement would hide a real error (ADR 0105 §3.3, financial F-3,
    /// load-bearing).
    /// </summary>
    BalanceSheetOutOfBalance,
}
