namespace Harborline.Api.Blocks.FinancialLedger.Models;

/// <summary>
/// The result of <see cref="Services.IConsolidationService.ConsolidateAsync"/>
/// (ADR 0105 §4.1): a consolidated / combined balance set across a reporting scope's
/// entities, carrying THREE retained bands (financial F-1) plus the elimination trace
/// and the reconciliation warnings.
/// </summary>
/// <remarks>
/// The three bands (financial F-1, combining-statement presentation, never a fused blob):
/// <list type="number">
/// <item><see cref="PerEntityColumns"/> — one un-netted column per in-scope entity, never collapsed.</item>
/// <item><see cref="OwnedGroupSubtotal"/> — root + owned subs, inter-co + investment-in-sub eliminated, equity rolled to parent (§3.4). The true ASC 810 consolidation.</item>
/// <item><see cref="CombinedTotal"/> — owned-group subtotal + common-control siblings (peers; inter-entity transactions eliminated, equity side-by-side). Equals <see cref="OwnedGroupSubtotal"/> when the scope has no siblings.</item>
/// </list>
/// <para>
/// Both the consolidated balance sheet and consolidated P&amp;L are ordinary cartridges
/// run over a band's balance map — whether the map came from one chart or this
/// consolidation is transparent to them (§4.1).
/// </para>
/// <para>
/// <b>No balancing plug (financial F-3, load-bearing).</b> A non-tying band surfaces a
/// <see cref="ConsolidationWarningKind.BalanceSheetOutOfBalance"/> warning and is rendered
/// as-is; it is NEVER forced to zero by a silent compensating entry.
/// </para>
/// </remarks>
/// <param name="PerEntityColumns">Un-netted per-entity columns; never collapsed (§4.1, F-1).</param>
/// <param name="OwnedGroupSubtotal">Signed net balances for the owned group after elimination + equity rollup (§3.4).</param>
/// <param name="CombinedTotal">Signed net balances for the whole scope (owned group + siblings) after elimination.</param>
/// <param name="Eliminations">Every elimination removed, band-stamped, for audit/transparency (§3.3).</param>
/// <param name="Warnings">Reconciliation residuals surfaced visibly, never silently corrected (§3.3, F-3/F-6).</param>
/// <param name="Basis">The single reporting basis applied across the whole scope (§5, F-5).</param>
public sealed record ConsolidatedBalanceSet(
    IReadOnlyList<EntityBalanceColumn> PerEntityColumns,
    IReadOnlyDictionary<GLAccountId, decimal> OwnedGroupSubtotal,
    IReadOnlyDictionary<GLAccountId, decimal> CombinedTotal,
    IReadOnlyList<EliminationEntry> Eliminations,
    IReadOnlyList<ConsolidationWarning> Warnings,
    ReportingBasis Basis);
