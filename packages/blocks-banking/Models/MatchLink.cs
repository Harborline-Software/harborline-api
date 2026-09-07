using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.MultiTenancy;

namespace Harborline.Api.Blocks.Banking.Models;

/// <summary>
/// A link entity representing the many-to-many relationship between a
/// <see cref="StatementLine"/> and a ledger transaction (journal entry).
/// Per ADR 0112 Part 1 §2 fin-acct C3.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why many-to-many with per-link amount:</strong>
/// A single <c>LedgerTransactionRef?</c> on <see cref="StatementLine"/> cannot
/// represent the two real reconciliation shapes:
/// <list type="bullet">
///   <item>
///     <description>
///     <strong>Split</strong> — one statement line → N ledger postings
///     (e.g. a deposit bundling three invoice payments).
///     </description>
///   </item>
///   <item>
///     <description>
///     <strong>Aggregate</strong> — N statement lines → one ledger transaction
///     (e.g. a batched payout).
///     </description>
///   </item>
/// </list>
/// </para>
/// <para>
/// <strong>Reconciliation invariant (checkable):</strong> a statement line is
/// fully <see cref="ReconciliationState.Matched"/> only when the sum of its
/// <see cref="MatchLinkState.Accepted"/> link <see cref="Amount"/> values equals
/// <c>StatementLine.Amount</c>. A non-zero-but-incomplete sum is
/// <see cref="ReconciliationState.PartiallyMatched"/>.
/// </para>
/// <para>
/// <strong>Reverse-not-delete:</strong> un-matching transitions the link to
/// <see cref="MatchLinkState.Reversed"/>; the row is never deleted.
/// <em>No provenance is captured.</em> ADR 0112 fin-acct N4 calls for the durable
/// audit layer to record the principal + timestamp of acceptance and reversal, but
/// that is UNIMPLEMENTED — there is no principal column and no audit row.
/// Tracked by earlier repository ticket #3465. Do not answer an auditor from this type.
/// </para>
/// <para>
/// <strong>Amount:</strong> the portion of the match this link accounts for (signed; positive = credit).
/// Enables partial/split reconciliation. For a simple 1:1 match this equals the statement line's full amount.
/// </para>
/// <para>
/// <strong>AcceptedAt:</strong> when this link was accepted (transitioned from Proposed). Null while Proposed.
/// </para>
/// </remarks>
public sealed record MatchLink(
    MatchLinkId Id,
    TenantId TenantId,
    StatementLineId StatementLine,
    LedgerTransactionRef LedgerTransaction,
    decimal Amount,
    MatchLinkState State,
    Instant? AcceptedAt) : IMustHaveTenant;
