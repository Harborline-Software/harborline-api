namespace Harborline.Api.Blocks.FinancialLedger.Models;

/// <summary>
/// Classifies how a matched pair of inter-company balances is removed when a
/// reporting group is consolidated (ADR 0105 §3.2). The elimination ruleset
/// (<see cref="EliminationRule"/>) maps inter-company <see cref="AccountSubtype"/>
/// pairs to one of these kinds; the consolidation service (W0-6) applies them.
/// </summary>
public enum EliminationKind
{
    /// <summary>
    /// Due-from (asset) ↔ due-to (liability) net to zero. Covers trade
    /// payables/receivables AND inter-company loans (note receivable ↔ note payable).
    /// </summary>
    BalanceSheetClearing,

    /// <summary>
    /// Intra-group revenue ↔ matching expense net to zero (e.g. management fee,
    /// inter-company rent). Keyed off the counterparty stamp + existing
    /// revenue/expense subtypes rather than a dedicated subtype pair, so it is
    /// applied by the consolidation service (W0-6), not by a static
    /// <see cref="EliminationRule"/>.
    /// </summary>
    IncomeStatementMatch,

    /// <summary>
    /// Parent's investment (asset) ↔ the subsidiary's eliminated equity. Applies in
    /// the <c>Consolidated</c> scope ONLY; in a <c>Combined</c> presentation the
    /// investment and equity are NOT eliminated (ADR 0105 §3.2, §4).
    /// </summary>
    InvestmentInSubsidiary,
}
