using Harborline.Api.Blocks.Banking.Models;
using Harborline.Api.Blocks.FinancialLedger.Models;

namespace Harborline.Api.Blocks.Banking.Matching;

/// <summary>
/// A proposed match between a statement line and a ledger transaction candidate.
/// Proposals are ranked by <see cref="Score"/> — higher is better.
/// </summary>
/// <remarks>
/// Proposals are NEVER automatically applied.
/// A human must call <see cref="AcceptMatchService.AcceptAsync"/> to confirm
/// the match and create the <see cref="MatchLink"/> (ADR 0112 §Financial-ledger invariant 1:
/// propose-never-auto-post).
/// </remarks>
/// <param name="StatementLine">The statement line this proposal targets.</param>
/// <param name="LedgerEntry">The candidate journal entry.</param>
/// <param name="MatchAmount">
/// The portion of the statement line amount this proposal accounts for.
/// For a simple 1:1 match this equals <c>StatementLine.Amount</c>.
/// For split/aggregate proposals the caller may adjust before accepting.
/// </param>
/// <param name="Score">
/// Confidence score (0.0–1.0). Callers present proposals in descending order.
/// </param>
/// <param name="MatchedByRuleId">
/// If a user-defined <see cref="MatchingRule"/> triggered this proposal,
/// the rule id; otherwise null (heuristic match).
/// </param>
public sealed record MatchProposal(
    StatementLine StatementLine,
    JournalEntry LedgerEntry,
    decimal MatchAmount,
    double Score,
    MatchingRuleId? MatchedByRuleId);
