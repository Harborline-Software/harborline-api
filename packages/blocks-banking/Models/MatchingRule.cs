using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Lifecycle;
using Harborline.Api.Foundation.MultiTenancy;

namespace Harborline.Api.Blocks.Banking.Models;

/// <summary>
/// A user-defined matching rule: "lines like X → account/category Y".
/// Per ADR 0112 Part 1 §5 — user-defined matching rules.
/// </summary>
/// <remarks>
/// <para>
/// Matching rules are <strong>suggestions feeding the matching engine</strong> —
/// they never produce silent auto-posts. A rule match sets
/// <see cref="ReconciliationState.Proposed"/> on the statement line; a human
/// confirms the ledger posting (ADR 0112 §Financial-ledger invariant 1).
/// </para>
/// <para>
/// Rules are <see cref="IArchivable"/> (recoverable) per ADR 0112 §Delete semantics:
/// the history of how lines were categorized matters for audit; retired rules are
/// archived, never hard-deleted.
/// </para>
/// <para>
/// <strong>IArchivable placement:</strong> same Option 1 (marker on record) rationale
/// as <see cref="BankAccount"/> — net-new entity, carries a real closed-at timestamp.
/// </para>
/// <para>
/// <strong>Parameter notes:</strong>
/// <c>Name</c> is the human-readable display label.
/// <c>DescriptionContains</c> is a case-insensitive substring pattern matched against
/// statement-line descriptions.
/// <c>AmountDirection</c> optionally constrains to credit (inflow/positive) or debit (outflow/negative).
/// <c>AmountMin</c>/<c>AmountMax</c> constrain the absolute amount (inclusive bounds; null = no bound).
/// <c>SuggestedAccount</c> is the proposed GL account for matched lines; operator confirms before post.
/// <c>Priority</c> — lower value wins when multiple rules match.
/// </para>
/// </remarks>
public sealed record MatchingRule(
    MatchingRuleId Id,
    TenantId TenantId,
    string Name,
    string? DescriptionContains,
    MatchingRuleAmountDirection? AmountDirection,
    decimal? AmountMin,
    decimal? AmountMax,
    LedgerAccountRef? SuggestedAccount,
    int Priority,
    Instant? ArchivedAt,
    Instant CreatedAtUtc,
    Instant UpdatedAtUtc) : IMustHaveTenant, IArchivable
{
    DateTimeOffset? IArchivable.ArchivedAt => ArchivedAt.HasValue
        ? (DateTimeOffset)ArchivedAt.Value
        : null;
}

/// <summary>
/// Amount direction constraint for a <see cref="MatchingRule"/>.
/// </summary>
public enum MatchingRuleAmountDirection
{
    /// <summary>Only match credit (inflow; positive amount) lines.</summary>
    Credit,

    /// <summary>Only match debit (outflow; negative amount) lines.</summary>
    Debit,
}
