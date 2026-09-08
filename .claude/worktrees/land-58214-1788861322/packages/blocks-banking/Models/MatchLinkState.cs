namespace Harborline.Api.Blocks.Banking.Models;

/// <summary>
/// Lifecycle state of a <see cref="MatchLink"/> (statement-line ↔ ledger-transaction pair).
/// Per ADR 0112 Part 1 §2 — MatchLink entity + fin-acct C3.
/// Un-match is reverse-not-delete: a link transitions to <see cref="Reversed"/>,
/// never row-deleted.
/// </summary>
public enum MatchLinkState
{
    /// <summary>
    /// Proposed by the matching engine; awaiting human confirmation.
    /// No ledger posting has been created yet.
    /// </summary>
    Proposed,

    /// <summary>
    /// Confirmed by a human. The associated ledger transaction is linked.
    /// The sum of Accepted link amounts for a statement line determines
    /// whether the line is <see cref="ReconciliationState.PartiallyMatched"/>
    /// or fully <see cref="ReconciliationState.Matched"/>.
    /// </summary>
    Accepted,

    /// <summary>
    /// Un-matched — the link was reversed. The row is retained, never deleted.
    /// <em>Who reversed it is not recorded anywhere.</em> ADR 0112 fin-acct N4
    /// calls for durable audit of the principal + timestamp; it is UNIMPLEMENTED.
    /// Tracked by earlier repository ticket #3465.
    /// </summary>
    Reversed,
}
