using Harborline.Api.Blocks.FinancialAr.Models;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.People.Foundation.Models;

namespace Harborline.Api.Blocks.Migration.Erpnext.Verification;

/// <summary>
/// The overall disposition of a Pass 6 run. Only the two halting conditions enumerated in
/// spec §4.6 "Failure modes" can move this off <see cref="Passed"/>.
/// </summary>
public enum VerificationOutcome
{
    /// <summary>All checks within tolerance (or aging drift accepted via the flag). Import stands.</summary>
    Passed = 1,

    /// <summary>
    /// Trial balance failed to sum to zero (spec §4.6 step 1, $0 tolerance). A hard halt — the
    /// orchestrator rolls back the entire run and surfaces the report. Defense-in-depth: cannot
    /// occur if Pass 4 rejected every imbalanced journal entry.
    /// </summary>
    TrialBalanceMismatch = 2,

    /// <summary>
    /// AR or AP aging diverged beyond the $0.01 per-party/per-bucket threshold and
    /// <see cref="VerificationOptions.AllowAgingDrift"/> was not set (spec §4.6 step 2/3).
    /// </summary>
    AgingReconciliationFailed = 3,
}

/// <summary>Trial-balance verification result (spec §4.6 step 1).</summary>
/// <remarks>
/// <see cref="SignedTotal"/> is the sum of every posted account's debit-minus-credit balance; the
/// double-entry invariant requires it to be exactly zero. <see cref="Subtotals"/> carries the
/// per-<see cref="GLAccountType"/> breakdown for the report table (Asset + Expense vs Liability +
/// Equity + Revenue). <see cref="UnclassifiedAccountCount"/> counts accounts whose type could not
/// be resolved — their balance still contributes to <see cref="SignedTotal"/> so the zero-check
/// stays exact, but they are surfaced separately so a missing-account-type bug is not masked.
/// </remarks>
public sealed record TrialBalanceResult(
    decimal SignedTotal,
    IReadOnlyList<AccountTypeSubtotal> Subtotals,
    int UnclassifiedAccountCount)
{
    /// <summary>True when the ledger balances exactly (spec mandates $0 tolerance).</summary>
    public bool IsBalanced => SignedTotal == 0m;
}

/// <summary>Per-account-type signed subtotal for the trial-balance report table.</summary>
/// <param name="Type">The accounting category.</param>
/// <param name="SignedTotal">Sum of debit-minus-credit balances for accounts of this type.</param>
public sealed record AccountTypeSubtotal(GLAccountType Type, decimal SignedTotal);

/// <summary>
/// AR or AP aging diff result (spec §4.6 step 2/3). <see cref="Checked"/> is <see langword="false"/>
/// when no snapshot file was supplied (the check is optional). <see cref="ExceedingRows"/> lists
/// only the party/bucket combinations whose absolute diff exceeded $0.01.
/// </summary>
public sealed record AgingDiffResult(bool Checked, IReadOnlyList<PartyAgingDiff> ExceedingRows)
{
    /// <summary>An aging diff result for a check that was skipped (no snapshot supplied).</summary>
    public static AgingDiffResult NotChecked { get; } = new(false, Array.Empty<PartyAgingDiff>());

    /// <summary>True when the check ran and every bucket was within the $0.01 threshold.</summary>
    public bool WithinThreshold => ExceedingRows.Count == 0;
}

/// <summary>One party's out-of-threshold aging buckets.</summary>
/// <param name="PartyId">Customer (AR) or vendor (AP).</param>
/// <param name="Buckets">The buckets whose absolute diff exceeded the threshold.</param>
public sealed record PartyAgingDiff(PartyId PartyId, IReadOnlyList<AgingBucketDiff> Buckets);

/// <summary>A single bucket's expected/actual/diff for an aging comparison.</summary>
/// <param name="Bucket">Bucket label (e.g. <c>"Current"</c>, <c>"31-60"</c>).</param>
/// <param name="Expected">Snapshot (source ERPNext) balance.</param>
/// <param name="Actual">Anchor's recomputed balance.</param>
public sealed record AgingBucketDiff(string Bucket, decimal Expected, decimal Actual)
{
    /// <summary>Actual minus expected.</summary>
    public decimal Diff => Actual - Expected;
}

/// <summary>
/// Per-account balance diff result (spec §4.6 step 4). <see cref="Checked"/> is
/// <see langword="false"/> when no <c>gl-balances-snapshot.json</c> was supplied.
/// <see cref="Diffs"/> lists accounts whose absolute diff exceeded $0.01, sorted by absolute
/// difference descending (spec §4.6 step 6: "sorted by absolute difference").
/// </summary>
public sealed record AccountBalanceDiffResult(bool Checked, IReadOnlyList<AccountBalanceDiff> Diffs)
{
    /// <summary>A per-account diff result for a check that was skipped (no snapshot supplied).</summary>
    public static AccountBalanceDiffResult NotChecked { get; } = new(false, Array.Empty<AccountBalanceDiff>());

    /// <summary>True when the check ran and every account was within the $0.01 threshold.</summary>
    public bool WithinThreshold => Diffs.Count == 0;
}

/// <summary>One account's expected/actual signed-balance diff.</summary>
/// <param name="AccountCode">Human-readable account code (the cross-system join key).</param>
/// <param name="Expected">Snapshot (source ERPNext) signed balance, or <see langword="null"/> when the account is absent from the snapshot.</param>
/// <param name="Actual">Anchor's recomputed signed balance, or <see langword="null"/> when the account had no posted activity Anchor-side.</param>
public sealed record AccountBalanceDiff(string AccountCode, decimal? Expected, decimal? Actual)
{
    /// <summary>Actual minus expected, treating an absent side as zero.</summary>
    public decimal Diff => (Actual ?? 0m) - (Expected ?? 0m);
}

/// <summary>
/// Invoice-balance reconciliation result (spec §4.6 step 5): for each open/paid invoice the cached
/// <c>Balance</c> must equal <c>Total − AmountPaid</c>. Discrepancies are reported (not halted —
/// spec §4.6 lists only trial-balance + aging as halting conditions).
/// </summary>
/// <param name="InvoicesChecked">Count of Issued / PartiallyPaid / Paid invoices examined.</param>
/// <param name="Discrepancies">Invoices whose cached balance was internally inconsistent.</param>
public sealed record InvoiceBalanceCheckResult(int InvoicesChecked, IReadOnlyList<InvoiceBalanceDiscrepancy> Discrepancies);

/// <summary>One invoice whose cached <c>Balance</c> did not equal <c>Total − AmountPaid</c>.</summary>
/// <param name="InvoiceId">Invoice identity.</param>
/// <param name="InvoiceNumber">Customer-facing number for the report.</param>
/// <param name="Total">Cached invoice total.</param>
/// <param name="AmountPaid">Cached cumulative payment applied.</param>
/// <param name="RecordedBalance">The cached balance field as imported.</param>
public sealed record InvoiceBalanceDiscrepancy(
    InvoiceId InvoiceId,
    string InvoiceNumber,
    decimal Total,
    decimal AmountPaid,
    decimal RecordedBalance)
{
    /// <summary>The balance the cached fields imply.</summary>
    public decimal ExpectedBalance => Total - AmountPaid;
}

/// <summary>
/// The complete result of one <see cref="ErpnextVerificationPass"/> run. The report renderer
/// (<see cref="Reporting.MigrationReportRenderer"/>) consumes this to render the verification
/// sections; the orchestrator inspects <see cref="Outcome"/> to decide whether to roll the run back.
/// </summary>
public sealed record VerificationResult(
    VerificationOutcome Outcome,
    TrialBalanceResult TrialBalance,
    AgingDiffResult ArAging,
    AgingDiffResult ApAging,
    AccountBalanceDiffResult AccountBalances,
    InvoiceBalanceCheckResult InvoiceBalances)
{
    /// <summary>True when the run cleared every halting check.</summary>
    public bool IsPassed => Outcome == VerificationOutcome.Passed;
}
