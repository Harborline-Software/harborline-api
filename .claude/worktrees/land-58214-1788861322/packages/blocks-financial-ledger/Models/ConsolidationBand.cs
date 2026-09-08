namespace Harborline.Api.Blocks.FinancialLedger.Models;

/// <summary>
/// Which summary band of a <see cref="ConsolidatedBalanceSet"/> an
/// <see cref="EliminationEntry"/> or <see cref="ConsolidationWarning"/> was computed
/// for (ADR 0105 §4.1). Eliminations and residual warnings are <b>scope-relative</b>
/// (§3.2.1): the same posted inter-company pair can eliminate in the wider
/// <see cref="CombinedTotal"/> band yet remain un-eliminated in the narrower
/// <see cref="OwnedGroupSubtotal"/> band when its counterparty is a common-control
/// sibling outside the owned group — so each elimination / warning is stamped with the
/// band it belongs to rather than reported once for the whole result.
/// </summary>
public enum ConsolidationBand
{
    /// <summary>
    /// Root + owned subsidiaries (ownership chain): inter-company cleared,
    /// investment-in-subsidiary eliminated against the sub's equity, equity rolled to
    /// the parent (§3.4). The true ASC 810 single-economic-entity consolidation.
    /// </summary>
    OwnedGroupSubtotal,

    /// <summary>
    /// Owned group + common-control siblings: siblings presented as peers with their
    /// inter-entity transactions still eliminated but their equity side-by-side
    /// (§3.4, §4.2). The whole-portfolio combined total. Equals
    /// <see cref="OwnedGroupSubtotal"/> when the scope has no common-control siblings.
    /// </summary>
    CombinedTotal,
}
