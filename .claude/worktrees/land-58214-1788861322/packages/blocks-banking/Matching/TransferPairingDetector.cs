using Harborline.Api.Blocks.Banking.Models;
using Harborline.Api.Blocks.Banking.Services;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.Banking.Matching;

/// <summary>
/// Detects inter-account transfer pairs across the operator's own connected
/// bank accounts, preventing double-booking (ADR 0112 fin-acct C4).
/// </summary>
/// <remarks>
/// <para>
/// When an operator moves money between two of their own connected accounts,
/// both feeds show the movement (an outflow on account A, an inflow on account B).
/// The engine MUST propose the two legs as a single transfer/contra — NOT two
/// independent ledger postings that double-book the single economic transfer.
/// </para>
/// <para>
/// Pairing criteria: opposite-sign, same magnitude (within <see cref="AmountToleranceDecimal"/>),
/// date within <see cref="DateWindowDays"/> days, and both accounts belonging to
/// the same tenant.
/// </para>
/// <para>
/// Pairs are presented as <see cref="TransferPair"/> values. The human confirms
/// the transfer once via a single <see cref="AcceptMatchService.AcceptAsync"/> call
/// for each leg (sharing the same <c>LedgerTransactionRef</c>).
/// </para>
/// </remarks>
public sealed class TransferPairingDetector
{
    /// <summary>Amount tolerance for pairing: lines are considered same-magnitude when their
    /// absolute amounts differ by at most this value (handles minor exchange-rate rounding).</summary>
    public const decimal AmountToleranceDecimal = 0.01m;

    /// <summary>Date window: outflow and inflow must be within this many calendar days of each other.</summary>
    public const int DateWindowDays = 5;

    private readonly IStatementLineRepository _lineRepo;

    /// <summary>Initializes the detector with a statement-line repository for cross-account queries.</summary>
    public TransferPairingDetector(IStatementLineRepository lineRepo)
    {
        ArgumentNullException.ThrowIfNull(lineRepo);
        _lineRepo = lineRepo;
    }

    /// <summary>
    /// Detects transfer pairs for the given set of unmatched statement lines, searching
    /// for matching counterpart lines across other accounts of the same tenant.
    /// </summary>
    /// <param name="tenantId">Tenant scope.</param>
    /// <param name="candidateLines">Lines to pair (typically unmatched lines of one account).</param>
    /// <param name="allTenantAccounts">All bank accounts belonging to this tenant, used to query other accounts.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Transfer pairs found. Lines may appear in at most one pair.</returns>
    public async Task<IReadOnlyList<TransferPair>> DetectAsync(
        TenantId tenantId,
        IReadOnlyList<StatementLine> candidateLines,
        IReadOnlyList<BankAccount> allTenantAccounts,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidateLines);
        ArgumentNullException.ThrowIfNull(allTenantAccounts);

        if (candidateLines.Count == 0 || allTenantAccounts.Count < 2)
            return [];

        // Build a lookup of all unmatched lines across OTHER tenant accounts (excluding the candidate accounts)
        var candidateAccountIds = new HashSet<BankAccountId>(candidateLines.Select(l => l.AccountId));
        var otherAccounts = allTenantAccounts
            .Where(a => !candidateAccountIds.Contains(a.Id))
            .ToList();

        if (otherAccounts.Count == 0)
            return [];

        // Collect all unmatched lines from the other accounts
        var otherLines = new List<StatementLine>();
        foreach (var account in otherAccounts)
        {
            ct.ThrowIfCancellationRequested();
            var lines = await _lineRepo.ListByAccountAsync(tenantId, account.Id, ct).ConfigureAwait(false);
            otherLines.AddRange(lines.Where(l =>
                l.State == ReconciliationState.Unmatched ||
                l.State == ReconciliationState.Proposed));
        }

        // Match: outflow from candidate side with same-magnitude inflow from other side (or vice versa)
        var pairs = new List<TransferPair>();
        var usedCandidateIds = new HashSet<StatementLineId>();
        var usedOtherIds = new HashSet<StatementLineId>();

        foreach (var candidate in candidateLines)
        {
            if (usedCandidateIds.Contains(candidate.Id)) continue;
            if (candidate.State != ReconciliationState.Unmatched &&
                candidate.State != ReconciliationState.Proposed) continue;

            foreach (var other in otherLines)
            {
                if (usedOtherIds.Contains(other.Id)) continue;

                // Must be opposite sign
                if (!AreOppositeSign(candidate.Amount, other.Amount)) continue;

                // Must be same magnitude within tolerance
                if (Math.Abs(Math.Abs(candidate.Amount) - Math.Abs(other.Amount)) > AmountToleranceDecimal) continue;

                // Must be near-date
                var candidateDate = (DateTimeOffset)candidate.PostedAt;
                var otherDate = (DateTimeOffset)other.PostedAt;
                if (Math.Abs((candidateDate.Date - otherDate.Date).TotalDays) > DateWindowDays) continue;

                // Pair found: outflow candidate, inflow other (or vice versa)
                TransferPair pair = candidate.Amount < 0
                    ? new TransferPair(OutflowLine: candidate, InflowLine: other)
                    : new TransferPair(OutflowLine: other, InflowLine: candidate);

                pairs.Add(pair);
                usedCandidateIds.Add(candidate.Id);
                usedOtherIds.Add(other.Id);
                break;
            }
        }

        return pairs;
    }

    private static bool AreOppositeSign(decimal a, decimal b)
        => (a > 0 && b < 0) || (a < 0 && b > 0);
}
