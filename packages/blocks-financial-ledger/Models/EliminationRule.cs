namespace Harborline.Api.Blocks.FinancialLedger.Models;

/// <summary>
/// One inter-company elimination rule (ADR 0105 §3.2): a pair of account subtypes —
/// the asset/investment leg on the creditor/parent entity and the matching
/// liability/equity leg on the debtor/subsidiary entity — and the
/// <see cref="EliminationKind"/> that says how the consolidation service nets them.
/// </summary>
/// <param name="DueFromSubtype">Asset/investment leg on the creditor/parent entity.</param>
/// <param name="DueToSubtype">Matching liability/equity leg on the debtor/subsidiary entity.</param>
/// <param name="Kind">How the matched pair is eliminated.</param>
public sealed record EliminationRule(
    AccountSubtype DueFromSubtype,
    AccountSubtype DueToSubtype,
    EliminationKind Kind);
