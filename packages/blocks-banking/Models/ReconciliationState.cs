namespace Harborline.Api.Blocks.Banking.Models;

/// <summary>
/// The reconciliation state of a <see cref="StatementLine"/>.
/// Per ADR 0112 Part 1 §2 — statement-line entity.
/// </summary>
public enum ReconciliationState
{
    /// <summary>
    /// No match has been proposed or accepted. Default state on ingestion.
    /// </summary>
    Unmatched,

    /// <summary>
    /// The matching engine has proposed a match. A human must confirm
    /// before the ledger posting is created (ADR 0112 §Financial-ledger invariant 1).
    /// </summary>
    Proposed,

    /// <summary>
    /// At least one <see cref="MatchLink"/> is Accepted but the sum of accepted
    /// link amounts does not yet equal <c>StatementLine.Amount</c>.
    /// </summary>
    PartiallyMatched,

    /// <summary>
    /// Fully reconciled — sum of Accepted <see cref="MatchLink"/> amounts equals
    /// <c>StatementLine.Amount</c>.
    /// </summary>
    Matched,

    /// <summary>
    /// Explicitly excluded from reconciliation (e.g., pre-cutover historical line,
    /// duplicate flagged by operator, or mis-import corrected via state transition).
    /// Excluded lines are append-only — they are never deleted.
    /// </summary>
    Excluded,
}
