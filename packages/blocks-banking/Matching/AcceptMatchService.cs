using Harborline.Api.Blocks.Banking.Models;
using Harborline.Api.Blocks.Banking.Services;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialPeriods.Models;
using Harborline.Api.Blocks.FinancialPeriods.Services;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.Banking.Matching;

/// <summary>
/// Accepts a proposed <see cref="MatchLink"/>, enforcing all invariants before
/// transitioning the link to <see cref="MatchLinkState.Accepted"/> and updating
/// the statement line's <see cref="ReconciliationState"/>.
/// Per ADR 0112 §Financial-ledger invariants.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Propose-never-auto-post (fin-acct invariant 1):</strong>
/// this service is the human-confirmation step. No ledger posting is created here —
/// the caller is responsible for creating or linking the journal entry before calling this service.
/// The accept creates the <see cref="MatchLink"/> link and updates reconciliation state.
/// </para>
/// <para>
/// <strong>Fiscal-period Locked gate (fin-acct invariant 3b):</strong>
/// if the statement line's <c>PostedAt</c> falls within a <see cref="FiscalPeriodStatus.Locked"/>
/// fiscal period, acceptance is rejected. The human must unlock the period first
/// via <c>IPeriodCloseService.UnlockAsync</c>. This gate has no bypass.
/// </para>
/// <para>
/// <strong>SoftClosed requires the explicit override operation (fin-acct invariant 3a):</strong>
/// this service does NOT block acceptance into a <see cref="FiscalPeriodStatus.SoftClosed"/>
/// period. Authorization is the caller's responsibility.
/// </para>
/// <para>
/// <strong>Bank-rec lock gate (independent of fiscal-period lock, fin-acct C1):</strong>
/// if the account + period already has a <see cref="BankReconciliationLockState.Locked"/>
/// <see cref="Reconciliation"/>, the accept is rejected. Un-reconciling requires an
/// explicit unlock-with-provenance action before re-matching.
/// </para>
/// <para>
/// <strong>Sum-integrity (fin-acct C3):</strong>
/// after acceptance the sum of all <see cref="MatchLinkState.Accepted"/> link amounts
/// is compared to <c>StatementLine.Amount</c>:
/// <list type="bullet">
///   <item><description>Equal → <see cref="ReconciliationState.Matched"/></description></item>
///   <item><description>Non-zero but less → <see cref="ReconciliationState.PartiallyMatched"/></description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class AcceptMatchService
{
    private readonly IMatchLinkRepository _linkRepo;
    private readonly IStatementLineRepository _lineRepo;
    private readonly IReconciliationRepository _reconciliationRepo;
    private readonly IFiscalPeriodRepository _periodRepo;
    private readonly ReconciliationLockLease _reconciliationLease;
    private readonly TimeProvider _time;

    /// <summary>Initializes the accept-match service.</summary>
    public AcceptMatchService(
        IMatchLinkRepository linkRepo,
        IStatementLineRepository lineRepo,
        IReconciliationRepository reconciliationRepo,
        IFiscalPeriodRepository periodRepo,
        TimeProvider time,
        ReconciliationLockLease? reconciliationLease = null)
    {
        ArgumentNullException.ThrowIfNull(linkRepo);
        ArgumentNullException.ThrowIfNull(lineRepo);
        ArgumentNullException.ThrowIfNull(reconciliationRepo);
        ArgumentNullException.ThrowIfNull(periodRepo);

        _linkRepo           = linkRepo;
        _lineRepo           = lineRepo;
        _reconciliationRepo = reconciliationRepo;
        _periodRepo         = periodRepo;
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _reconciliationLease = reconciliationLease ?? new ReconciliationLockLease();
    }

    /// <summary>
    /// Accepts a proposed <see cref="MatchLink"/>.
    /// </summary>
    /// <param name="tenantId">Tenant scope.</param>
    /// <param name="matchLinkId">The <see cref="MatchLink"/> to accept.</param>
    /// <param name="fiscalPeriodId">
    /// The fiscal period the statement line falls into — used to check the
    /// fiscal-period Locked gate. Pass the period that covers the statement line's
    /// posted date. May be null if fiscal-period checking is not wired (falls back
    /// to no Locked check — use only in tests or non-fiscal tenants).
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="MatchAcceptException">When any invariant is violated.</exception>
    /// <exception cref="InvalidOperationException">When the link or statement line is not found.</exception>
    public async Task AcceptAsync(
        TenantId tenantId,
        MatchLinkId matchLinkId,
        FiscalPeriodId? fiscalPeriodId = null,
        CancellationToken ct = default)
    {
        // Load the link
        MatchLink? link = await _linkRepo.GetByIdAsync(tenantId, matchLinkId, ct).ConfigureAwait(false);
        if (link is null)
            throw new InvalidOperationException($"MatchLink '{matchLinkId.Value}' not found.");

        if (link.State != MatchLinkState.Proposed)
            throw new MatchAcceptException(matchLinkId, MatchAcceptRejectReason.LinkNotProposed,
                $"MatchLink '{matchLinkId.Value}' is in state {link.State}; only Proposed links can be accepted.");

        // Load the statement line
        StatementLine? line = await _lineRepo.GetByIdAsync(tenantId, link.StatementLine, ct).ConfigureAwait(false);
        if (line is null)
            throw new InvalidOperationException($"StatementLine '{link.StatementLine.Value}' not found.");

        // ── Gate: fiscal-period Locked (fin-acct invariant 3b) ──
        if (fiscalPeriodId is not null)
        {
            FiscalPeriodId pid = fiscalPeriodId.Value;
            FiscalPeriod? period = await _periodRepo.GetAsync(pid, ct).ConfigureAwait(false);
            if (period is not null && period.Status == FiscalPeriodStatus.Locked)
                throw new MatchAcceptException(matchLinkId, MatchAcceptRejectReason.FiscalPeriodLocked,
                    $"Cannot accept match: fiscal period '{pid.Value}' is Locked. " +
                    "Unlock the period via IPeriodCloseService.UnlockAsync before accepting.");
        }

        // ── Gate: bank-rec lock (fin-acct C1 — independent of fiscal-period) ──
        if (fiscalPeriodId is not null)
        {
            FiscalPeriodId pid = fiscalPeriodId.Value;
            Reconciliation? rec = await _reconciliationRepo.GetByAccountPeriodAsync(
                tenantId, line.AccountId, pid, ct).ConfigureAwait(false);
            // The lock is a bounded LEASE, not an indefinite flag. Reading LockState directly here
            // would keep the permanent wedge this card exists to remove: a holder that crashed, or a
            // migrated row with no timestamp, would block accept for ever while the lock route and
            // un-match both recovered. Every consumer of the lock asks the lease, not the column.
            if (rec is not null && _reconciliationLease.IsHeld(rec))
                throw new MatchAcceptException(matchLinkId, MatchAcceptRejectReason.BankRecLocked,
                    $"Cannot accept match: reconciliation for account '{line.AccountId.Value}' " +
                    $"in period '{pid.Value}' is bank-rec locked. " +
                    "Un-reconcile first via explicit unlock-with-provenance action.");
        }

        // ── Accept the link ──
        var accepted = link with
        {
            State      = MatchLinkState.Accepted,
            AcceptedAt = new Instant(_time.GetUtcNow()),
        };
        await _linkRepo.UpdateAsync(accepted, ct).ConfigureAwait(false);

        // ── Sum-integrity: compute new reconciliation state ──
        var allLinks = await _linkRepo.ListByStatementLineAsync(tenantId, line.Id, ct).ConfigureAwait(false);
        decimal acceptedSum = allLinks
            .Where(l => l.State == MatchLinkState.Accepted)
            .Sum(l => l.Amount);

        ReconciliationState newState = ComputeNewState(line.Amount, acceptedSum);

        var updatedLine = line with { State = newState };
        await _lineRepo.UpdateAsync(updatedLine, ct).ConfigureAwait(false);
    }

    private static ReconciliationState ComputeNewState(decimal lineAmount, decimal acceptedSum)
    {
        // Use a small tolerance for floating-point equality
        if (Math.Abs(acceptedSum - lineAmount) < 0.005m)
            return ReconciliationState.Matched;

        if (Math.Abs(acceptedSum) > 0.005m)
            return ReconciliationState.PartiallyMatched;

        return ReconciliationState.Proposed;
    }
}

/// <summary>
/// Thrown when <see cref="AcceptMatchService.AcceptAsync"/> rejects an acceptance
/// due to a violated invariant.
/// </summary>
public sealed class MatchAcceptException : Exception
{
    /// <summary>The match link that was rejected.</summary>
    public MatchLinkId MatchLinkId { get; }

    /// <summary>Reason the acceptance was rejected.</summary>
    public MatchAcceptRejectReason Reason { get; }

    /// <summary>Initializes the exception.</summary>
    public MatchAcceptException(MatchLinkId id, MatchAcceptRejectReason reason, string message)
        : base(message)
    {
        MatchLinkId = id;
        Reason = reason;
    }
}

/// <summary>Categorized reasons <see cref="AcceptMatchService"/> may reject an acceptance.</summary>
public enum MatchAcceptRejectReason
{
    /// <summary>The link is not in <see cref="MatchLinkState.Proposed"/> state.</summary>
    LinkNotProposed,

    /// <summary>The target fiscal period is <see cref="FiscalPeriodStatus.Locked"/>.</summary>
    FiscalPeriodLocked,

    /// <summary>The account + period reconciliation is bank-rec locked.</summary>
    BankRecLocked,
}
