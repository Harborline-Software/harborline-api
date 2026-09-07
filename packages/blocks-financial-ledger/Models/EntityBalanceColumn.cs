namespace Harborline.Api.Blocks.FinancialLedger.Models;

/// <summary>
/// One un-netted per-entity column in a <see cref="ConsolidatedBalanceSet"/>
/// (ADR 0105 §4.1, financial F-1). The per-entity columns are ALWAYS retained and
/// NEVER collapsed into a single fused statement — a combining-statement presentation
/// keeps every entity visible (§4.2). These balances are <b>raw</b> (pre-elimination):
/// the elimination transform applies to the summary bands, not to the source columns,
/// because each entity's own books stay pristine and standalone (§3.3).
/// </summary>
/// <param name="EntityId">The legal entity this column reports.</param>
/// <param name="ChartId">The chart of accounts the entity's balances were read from.</param>
/// <param name="Presentation">
/// How this entity rolls up: <see cref="ConsolidationPresentation.Consolidated"/> for an
/// owned subsidiary (or the root), <see cref="ConsolidationPresentation.Combined"/> for a
/// common-control sibling peer.
/// </param>
/// <param name="Balances">Signed raw balances per account (debit − credit), pre-elimination.</param>
public sealed record EntityBalanceColumn(
    LegalEntityId EntityId,
    ChartOfAccountsId ChartId,
    ConsolidationPresentation Presentation,
    IReadOnlyDictionary<GLAccountId, decimal> Balances);
