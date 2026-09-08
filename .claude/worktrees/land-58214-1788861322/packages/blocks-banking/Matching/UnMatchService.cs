using Harborline.Api.Blocks.Banking.Models;
using Harborline.Api.Blocks.Banking.Services;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.Banking.Matching;

/// <summary>
/// Reverses an accepted <see cref="MatchLink"/> without deleting it (un-match).
/// Per ADR 0112 §Delete semantics: un-match is reverse-not-delete.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Reverse-not-delete (ADR 0112 fin-acct N4):</strong>
/// un-matching transitions the link to <see cref="MatchLinkState.Reversed"/>.
/// The link is NEVER row-deleted.
/// </para>
/// <para>
/// <strong>State rollback:</strong> after reversal the sum of remaining accepted link
/// amounts is recomputed. If no accepted links remain, the statement line returns to
/// <see cref="ReconciliationState.Unmatched"/>; if some accepted links remain, the
/// line transitions to <see cref="ReconciliationState.PartiallyMatched"/>.
/// </para>
/// <para>
/// <strong>Bank-rec lock (fin-acct C1):</strong>
/// un-matching into a bank-rec locked reconciliation is prevented. The reconciliation
/// must be explicitly unlocked before un-matching can proceed.
/// </para>
/// </remarks>
public sealed class UnMatchService
{
    private readonly IMatchLinkRepository _linkRepo;
    private readonly IStatementLineRepository _lineRepo;
    private readonly IReconciliationRepository _reconciliationRepo;
    private readonly ReconciliationLockLease _reconciliationLease;

    /// <summary>Initializes the un-match service.</summary>
    public UnMatchService(
        IMatchLinkRepository linkRepo,
        IStatementLineRepository lineRepo,
        IReconciliationRepository reconciliationRepo,
        ReconciliationLockLease? reconciliationLease = null)
    {
        ArgumentNullException.ThrowIfNull(linkRepo);
        ArgumentNullException.ThrowIfNull(lineRepo);
        ArgumentNullException.ThrowIfNull(reconciliationRepo);

        _linkRepo           = linkRepo;
        _lineRepo           = lineRepo;
        _reconciliationRepo = reconciliationRepo;
        _reconciliationLease = reconciliationLease ?? new ReconciliationLockLease();
    }

    /// <summary>
    /// Reverses a <see cref="MatchLink"/>, setting it to <see cref="MatchLinkState.Reversed"/>.
    /// </summary>
    /// <param name="tenantId">Tenant scope.</param>
    /// <param name="matchLinkId">The <see cref="MatchLink"/> to un-match.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="UnMatchException">When any invariant is violated.</exception>
    /// <exception cref="InvalidOperationException">When the link or statement line is not found.</exception>
    public async Task UnMatchAsync(
        TenantId tenantId,
        MatchLinkId matchLinkId,
        CancellationToken ct = default)
    {
        // Load the link
        MatchLink? link = await _linkRepo.GetByIdAsync(tenantId, matchLinkId, ct).ConfigureAwait(false);
        if (link is null)
            throw new InvalidOperationException($"MatchLink '{matchLinkId.Value}' not found.");

        // Only Accepted links can be reversed (Proposed links are simply abandoned, not reversed)
        if (link.State == MatchLinkState.Reversed)
            throw new UnMatchException(matchLinkId, UnMatchRejectReason.AlreadyReversed,
                $"MatchLink '{matchLinkId.Value}' is already Reversed.");

        if (link.State != MatchLinkState.Accepted)
            throw new UnMatchException(matchLinkId, UnMatchRejectReason.LinkNotAccepted,
                $"MatchLink '{matchLinkId.Value}' is in state {link.State}; only Accepted links can be un-matched.");

        // Load the statement line for bank-rec lock check
        StatementLine? line = await _lineRepo.GetByIdAsync(tenantId, link.StatementLine, ct).ConfigureAwait(false);
        if (line is null)
            throw new InvalidOperationException($"StatementLine '{link.StatementLine.Value}' not found.");

        // ── Gate: bank-rec lock (fin-acct C1) ──
        // We don't know the period here directly; check all reconciliations for this account
        var recs = await _reconciliationRepo.ListByAccountAsync(tenantId, line.AccountId, ct).ConfigureAwait(false);
        var lineDate = ((DateTimeOffset)line.PostedAt).Date;
        foreach (var rec in recs)
        {
            if (_reconciliationLease.IsHeld(rec))
            {
                // The reconciliation is locked — reject unless the line is not in this period
                // (Fine-grained period-date check belongs to the caller with FiscalPeriodId;
                // for safety, if ANY reconciliation on this account is locked we reject.
                // In practice callers should pass period context for surgical rejection.)
                throw new UnMatchException(matchLinkId, UnMatchRejectReason.BankRecLocked,
                    $"Cannot un-match: account '{line.AccountId.Value}' has a bank-rec locked reconciliation. " +
                    "Unlock the reconciliation first.");
            }
        }

        // ── Reverse the link ──
        var reversed = link with { State = MatchLinkState.Reversed };
        await _linkRepo.UpdateAsync(reversed, ct).ConfigureAwait(false);

        // ── State rollback: recompute reconciliation state from remaining accepted links ──
        var allLinks = await _linkRepo.ListByStatementLineAsync(tenantId, line.Id, ct).ConfigureAwait(false);
        decimal remainingAccepted = allLinks
            .Where(l => l.State == MatchLinkState.Accepted)
            .Sum(l => l.Amount);

        ReconciliationState newState = remainingAccepted == 0
            ? ReconciliationState.Unmatched
            : ReconciliationState.PartiallyMatched;

        var updatedLine = line with { State = newState };
        await _lineRepo.UpdateAsync(updatedLine, ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Thrown when <see cref="UnMatchService.UnMatchAsync"/> rejects an un-match
/// due to a violated invariant.
/// </summary>
public sealed class UnMatchException : Exception
{
    /// <summary>The match link that was rejected.</summary>
    public MatchLinkId MatchLinkId { get; }

    /// <summary>Reason the un-match was rejected.</summary>
    public UnMatchRejectReason Reason { get; }

    /// <summary>Initializes the exception.</summary>
    public UnMatchException(MatchLinkId id, UnMatchRejectReason reason, string message)
        : base(message)
    {
        MatchLinkId = id;
        Reason = reason;
    }
}

/// <summary>Categorized reasons <see cref="UnMatchService"/> may reject an un-match.</summary>
public enum UnMatchRejectReason
{
    /// <summary>The link is already in <see cref="MatchLinkState.Reversed"/> state.</summary>
    AlreadyReversed,

    /// <summary>The link is not in <see cref="MatchLinkState.Accepted"/> state.</summary>
    LinkNotAccepted,

    /// <summary>The account has a bank-rec locked reconciliation; unlock first.</summary>
    BankRecLocked,
}
