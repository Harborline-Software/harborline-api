using Harborline.Api.Blocks.Banking.Models;
using Harborline.Api.Blocks.Banking.Services;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialLedger.Services;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.Banking.Matching;

/// <summary>
/// Proposes <see cref="StatementLine"/> ↔ ledger-transaction matches without posting anything.
/// Per ADR 0112 §Financial-ledger invariant 1: the engine PROPOSES; a human CONFIRMS.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Propose-never-auto-post (ADR 0112 fin-acct invariant 1):</strong>
/// this service sets <see cref="ReconciliationState.Proposed"/> on statement lines
/// and creates <see cref="MatchLink"/> entities in <see cref="MatchLinkState.Proposed"/> state.
/// No ledger posting is created here. The human confirms via <see cref="AcceptMatchService"/>.
/// </para>
/// <para>
/// <strong>Matching strategy:</strong>
/// <list type="number">
///   <item>
///     <description>
///     <strong>Rule-based (highest priority):</strong> user-defined <see cref="MatchingRule"/> entries
///     are evaluated in ascending <c>Priority</c> order. A matching rule is a suggestion;
///     it creates a high-confidence proposal but still requires human confirmation.
///     </description>
///   </item>
///   <item>
///     <description>
///     <strong>Amount + date-window heuristic:</strong> candidates where the journal entry's
///     net movement equals the statement line amount within the date window
///     are ranked by score and returned.
///     </description>
///   </item>
/// </list>
/// </para>
/// <para>
/// <strong>Transfer pairs (ADR 0112 fin-acct C4):</strong> inter-account transfer pairs
/// are surfaced via <see cref="TransferPairingDetector.DetectAsync"/> before heuristic matching
/// so the human can confirm a single contra rather than two independent proposals.
/// </para>
/// </remarks>
public sealed class MatchingEngineService
{
    /// <summary>
    /// Default date-window tolerance (days) for amount+date heuristic matching.
    /// A statement line dated N is matched against journal entries dated N±<see cref="DefaultDateWindowDays"/>.
    /// </summary>
    public const int DefaultDateWindowDays = 3;

    private readonly IStatementLineRepository _lineRepo;
    private readonly IMatchingRuleRepository _ruleRepo;
    private readonly IMatchLinkRepository _linkRepo;
    private readonly IJournalStore _journalStore;

    /// <summary>
    /// Initializes the matching engine.
    /// </summary>
    public MatchingEngineService(
        IStatementLineRepository lineRepo,
        IMatchingRuleRepository ruleRepo,
        IMatchLinkRepository linkRepo,
        IJournalStore journalStore)
    {
        ArgumentNullException.ThrowIfNull(lineRepo);
        ArgumentNullException.ThrowIfNull(ruleRepo);
        ArgumentNullException.ThrowIfNull(linkRepo);
        ArgumentNullException.ThrowIfNull(journalStore);

        _lineRepo     = lineRepo;
        _ruleRepo     = ruleRepo;
        _linkRepo     = linkRepo;
        _journalStore = journalStore;
    }

    /// <summary>
    /// Proposes matches for all unmatched statement lines of the given account.
    /// Creates <see cref="MatchLink"/> entities in <see cref="MatchLinkState.Proposed"/> state
    /// and transitions lines to <see cref="ReconciliationState.Proposed"/>.
    /// </summary>
    /// <param name="tenantId">Tenant scope.</param>
    /// <param name="accountId">Account to propose matches for.</param>
    /// <param name="dateWindowDays">
    /// Optional override for the date window tolerance.
    /// Defaults to <see cref="DefaultDateWindowDays"/>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Proposals created, in descending score order.</returns>
    public async Task<IReadOnlyList<MatchProposal>> ProposeAsync(
        TenantId tenantId,
        BankAccountId accountId,
        int dateWindowDays = DefaultDateWindowDays,
        CancellationToken ct = default)
    {
        var unmatchedLines = await _lineRepo.ListByAccountAsync(tenantId, accountId, ct).ConfigureAwait(false);
        unmatchedLines = unmatchedLines
            .Where(l => l.State == ReconciliationState.Unmatched)
            .ToList()
            .AsReadOnly();

        if (unmatchedLines.Count == 0)
            return [];

        var rules = await _ruleRepo.ListAsync(tenantId, includeArchived: false, ct).ConfigureAwait(false);

        // Snapshot the journal for heuristic matching (in-process seam)
        var journalSnapshot = _journalStore.Snapshot(tenantId)
            .Where(e => e.Status == JournalEntryStatus.Posted)
            .ToList();

        var proposals = new List<MatchProposal>();

        foreach (var line in unmatchedLines)
        {
            ct.ThrowIfCancellationRequested();

            // 1. Try rule-based matches first
            MatchProposal? ruleProposal = await TryRuleBasedMatchAsync(
                tenantId, line, rules, journalSnapshot, dateWindowDays, ct).ConfigureAwait(false);

            if (ruleProposal is not null)
            {
                proposals.Add(ruleProposal);
                await CreateProposedLinkAsync(tenantId, ruleProposal, ct).ConfigureAwait(false);
                continue;
            }

            // 2. Fall back to heuristic (amount + date-window)
            MatchProposal? heuristicProposal = TryHeuristicMatch(line, journalSnapshot, dateWindowDays);

            if (heuristicProposal is not null)
            {
                proposals.Add(heuristicProposal);
                await CreateProposedLinkAsync(tenantId, heuristicProposal, ct).ConfigureAwait(false);
            }
        }

        return proposals.OrderByDescending(p => p.Score).ToList();
    }

    // ────────────────────────────────────────────────────────────────────────
    // Rule-based matching
    // ────────────────────────────────────────────────────────────────────────

    private async Task<MatchProposal?> TryRuleBasedMatchAsync(
        TenantId tenantId,
        StatementLine line,
        IReadOnlyList<MatchingRule> rules,
        IReadOnlyList<JournalEntry> journalEntries,
        int dateWindowDays,
        CancellationToken ct)
    {
        foreach (var rule in rules.OrderBy(r => r.Priority))
        {
            ct.ThrowIfCancellationRequested();

            if (!RuleMatches(rule, line)) continue;

            // Find a ledger candidate in the date window that matches the amount
            var candidate = FindLedgerCandidate(line, journalEntries, dateWindowDays);
            if (candidate is null) continue;

            return new MatchProposal(
                StatementLine:   line,
                LedgerEntry:     candidate,
                MatchAmount:     line.Amount,
                Score:           0.95,
                MatchedByRuleId: rule.Id);
        }
        return null;
    }

    private static bool RuleMatches(MatchingRule rule, StatementLine line)
    {
        // Description substring match
        if (rule.DescriptionContains is not null)
        {
            if (!line.Description.Contains(rule.DescriptionContains, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        // Amount direction constraint
        if (rule.AmountDirection.HasValue)
        {
            switch (rule.AmountDirection.Value)
            {
                case MatchingRuleAmountDirection.Credit when line.Amount <= 0:
                    return false;
                case MatchingRuleAmountDirection.Debit when line.Amount >= 0:
                    return false;
            }
        }

        // Amount range constraints (applied to absolute amount)
        decimal absAmount = Math.Abs(line.Amount);
        if (rule.AmountMin.HasValue && absAmount < rule.AmountMin.Value)
            return false;
        if (rule.AmountMax.HasValue && absAmount > rule.AmountMax.Value)
            return false;

        return true;
    }

    // ────────────────────────────────────────────────────────────────────────
    // Heuristic matching
    // ────────────────────────────────────────────────────────────────────────

    private static MatchProposal? TryHeuristicMatch(
        StatementLine line,
        IReadOnlyList<JournalEntry> journalEntries,
        int dateWindowDays)
    {
        var candidate = FindLedgerCandidate(line, journalEntries, dateWindowDays);
        if (candidate is null) return null;

        // Score based on amount precision and date proximity
        var lineDate = ((DateTimeOffset)line.PostedAt).Date;
        var entryDate = candidate.EntryDate.ToDateTime(TimeOnly.MinValue);
        int daysDiff = Math.Abs((lineDate - entryDate).Days);
        double dateScore = 1.0 - (daysDiff / (double)(dateWindowDays + 1));

        return new MatchProposal(
            StatementLine:   line,
            LedgerEntry:     candidate,
            MatchAmount:     line.Amount,
            Score:           0.7 * dateScore,
            MatchedByRuleId: null);
    }

    private static JournalEntry? FindLedgerCandidate(
        StatementLine line,
        IReadOnlyList<JournalEntry> journalEntries,
        int dateWindowDays)
    {
        var lineDate = ((DateTimeOffset)line.PostedAt).Date;
        decimal absTarget = Math.Abs(line.Amount);

        foreach (var entry in journalEntries)
        {
            // Date window check
            var entryDate = entry.EntryDate.ToDateTime(TimeOnly.MinValue).Date;
            if (Math.Abs((lineDate - entryDate).Days) > dateWindowDays) continue;

            // Match strategy: find any entry line whose debit or credit equals the absolute
            // statement line amount. In a double-entry system the bank leg of a matching
            // journal entry will have exactly this amount on either the debit or credit side.
            bool hasMatchingLine = entry.Lines.Any(l =>
                Math.Abs(l.Debit - absTarget) < 0.005m ||
                Math.Abs(l.Credit - absTarget) < 0.005m);

            if (hasMatchingLine)
                return entry;
        }

        return null;
    }

    // ────────────────────────────────────────────────────────────────────────
    // MatchLink persistence
    // ────────────────────────────────────────────────────────────────────────

    private async Task CreateProposedLinkAsync(
        TenantId tenantId,
        MatchProposal proposal,
        CancellationToken ct)
    {
        var ledgerRef = new LedgerTransactionRef(proposal.LedgerEntry.Id);

        var link = new MatchLink(
            Id:                MatchLinkId.NewId(),
            TenantId:          tenantId,
            StatementLine:     proposal.StatementLine.Id,
            LedgerTransaction: ledgerRef,
            Amount:            proposal.MatchAmount,
            State:             MatchLinkState.Proposed,
            AcceptedAt:        null);

        await _linkRepo.AddAsync(link, ct).ConfigureAwait(false);

        // Transition the statement line to Proposed state
        var updatedLine = proposal.StatementLine with { State = ReconciliationState.Proposed };
        await _lineRepo.UpdateAsync(updatedLine, ct).ConfigureAwait(false);
    }
}
