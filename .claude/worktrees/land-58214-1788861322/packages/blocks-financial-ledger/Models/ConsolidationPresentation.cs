namespace Harborline.Api.Blocks.FinancialLedger.Models;

/// <summary>
/// How a member entity rolls up into a reporting group (ADR 0104 §5, §6).
/// </summary>
public enum ConsolidationPresentation
{
    /// <summary>
    /// Owned subsidiary (transitive ownership edge from the root). Balances roll
    /// up into the consolidated group; intercompany positions are eliminated
    /// (elimination ruleset is W0-5 / ADR 0105 §3).
    /// </summary>
    Consolidated,

    /// <summary>
    /// Common-control sibling with no ownership edge from the root (e.g. the
    /// management S-corp in ADR 0104 §5). Presented side-by-side in a combined
    /// group — summed, not eliminated.
    /// </summary>
    Combined,

    /// <summary>
    /// Equity-method / minority band. Reserved vocabulary: the threshold policy
    /// that produces this band is deferred to ADR 0105 / W0-6; the W0-1 resolver
    /// never emits it.
    /// </summary>
    Equity,
}
