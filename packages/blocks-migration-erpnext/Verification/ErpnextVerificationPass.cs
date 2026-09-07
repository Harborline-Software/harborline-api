using Harborline.Api.Blocks.FinancialAp.Services;
using Harborline.Api.Blocks.FinancialAr.Models;
using Harborline.Api.Blocks.FinancialAr.Services;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialLedger.Services;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Foundation.Assets.Common;
using ArModels = Harborline.Api.Blocks.FinancialAr.Models;
using ApModels = Harborline.Api.Blocks.FinancialAp.Models;

namespace Harborline.Api.Blocks.Migration.Erpnext.Verification;

/// <summary>
/// A6 / importer Pass 6 — verification + reconciliation (spec §4.6). Proves the import preserved
/// the source financial position by re-deriving Anchor's books and diffing against CO-prepared
/// snapshots.
/// </summary>
/// <remarks>
/// <para>
/// <b>Strictly read-only.</b> Pass 6 issues no writes (spec §4.6 "Commit boundary: Read-only").
/// It runs four checks and returns a <see cref="VerificationResult"/>; the orchestrator (A7)
/// renders the report from that result (plus its own run-summary / reject-bin tracking via
/// <see cref="Reporting.MigrationReportRenderer"/>) and, if the result is a halting outcome,
/// rolls the run back.
/// </para>
/// <list type="number">
///   <item><b>Trial balance</b> — Σ(debit − credit) over every posted account must be exactly
///         zero ($0 tolerance). A non-zero sum is a hard halt
///         (<see cref="VerificationOutcome.TrialBalanceMismatch"/>).</item>
///   <item><b>AR / AP aging</b> — recompute Anchor's per-party aging buckets and diff against the
///         optional <c>ar-aging-snapshot.json</c> / <c>ap-aging-snapshot.json</c>. A diff over
///         $0.01 per party per bucket halts with
///         <see cref="VerificationOutcome.AgingReconciliationFailed"/> unless
///         <see cref="VerificationOptions.AllowAgingDrift"/> is set.</item>
///   <item><b>Per-account balance</b> — diff Anchor's per-account journal-line sums against the
///         optional <c>gl-balances-snapshot.json</c> ($0.01 per account). Reported, not halted.</item>
///   <item><b>Invoice balance</b> — for each open/paid invoice, assert the cached <c>Balance</c>
///         equals <c>Total − AmountPaid</c>. Reported, not halted.</item>
/// </list>
/// <para>
/// The CO-prepared snapshots are optional files in the export root; the orchestrator locates +
/// JSON-parses them and passes the parsed structures (or <see langword="null"/>) to
/// <see cref="RunAsync"/>. Keeping filesystem + parsing in the orchestrator leaves Pass 6 pure and
/// unit-testable without touching disk.
/// </para>
/// </remarks>
public sealed class ErpnextVerificationPass
{
    private const decimal MoneyTolerance = 0.01m;
    private static readonly string[] BucketLabels = { "Current", "0-30", "31-60", "61-90", "90+" };

    private readonly IGeneralLedgerReadModel _ledger;
    private readonly IAccountResolver _accounts;
    private readonly IArAgingService _arAging;
    private readonly IApAgingService _apAging;
    private readonly IInvoiceRepository _invoices;

    public ErpnextVerificationPass(
        IGeneralLedgerReadModel ledger,
        IAccountResolver accounts,
        IArAgingService arAging,
        IApAgingService apAging,
        IInvoiceRepository invoices)
    {
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _arAging = arAging ?? throw new ArgumentNullException(nameof(arAging));
        _apAging = apAging ?? throw new ArgumentNullException(nameof(apAging));
        _invoices = invoices ?? throw new ArgumentNullException(nameof(invoices));
    }

    /// <summary>
    /// Run Pass 6 over the supplied tenant + chart as of <paramref name="asOf"/>.
    /// </summary>
    /// <param name="tenantId">Tenant whose books are verified.</param>
    /// <param name="chartId">Chart-of-accounts the import landed against.</param>
    /// <param name="asOf">As-of date for the balance + aging projections (typically the export date).</param>
    /// <param name="arSnapshot">Optional CO-prepared AR aging snapshot; <see langword="null"/> skips the AR aging check.</param>
    /// <param name="apSnapshot">Optional CO-prepared AP aging snapshot; <see langword="null"/> skips the AP aging check.</param>
    /// <param name="glSnapshot">Optional CO-prepared per-account balance snapshot; <see langword="null"/> skips the per-account check.</param>
    /// <param name="options">Verification options (aging-drift acceptance); defaults to <see cref="VerificationOptions.Default"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<VerificationResult> RunAsync(
        TenantId tenantId,
        ChartOfAccountsId chartId,
        DateOnly asOf,
        ArAgingSnapshot? arSnapshot = null,
        ApAgingSnapshot? apSnapshot = null,
        GlBalancesSnapshot? glSnapshot = null,
        VerificationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= VerificationOptions.Default;

        // Load posted per-account signed balances once; the snapshot marker is forwarded opaquely
        // (Phase 1 ledger implementations ignore it per IGeneralLedgerReadModel).
        var balances = await _ledger
            .GetAccountBalancesAsOfAsync(tenantId, chartId, asOf, snapshotMarker: string.Empty, cancellationToken)
            .ConfigureAwait(false);

        // Resolve each posted account once; reused by trial-balance grouping + per-account diff.
        var resolved = new Dictionary<GLAccountId, GLAccount>(balances.Count);
        foreach (var accountId in balances.Keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var account = await _accounts.GetAsync(accountId, cancellationToken).ConfigureAwait(false);
            if (account is not null)
            {
                resolved[accountId] = account;
            }
        }

        var trialBalance = ComputeTrialBalance(balances, resolved);
        var arAging = await ComputeArAgingAsync(chartId, asOf, arSnapshot, cancellationToken).ConfigureAwait(false);
        var apAging = await ComputeApAgingAsync(chartId, asOf, apSnapshot, cancellationToken).ConfigureAwait(false);
        var accountBalances = ComputeAccountBalanceDiff(balances, resolved, glSnapshot);
        var invoiceBalances = await ComputeInvoiceBalanceCheckAsync(tenantId, chartId, cancellationToken).ConfigureAwait(false);

        var outcome = DetermineOutcome(trialBalance, arAging, apAging, options);

        return new VerificationResult(outcome, trialBalance, arAging, apAging, accountBalances, invoiceBalances);
    }

    private static TrialBalanceResult ComputeTrialBalance(
        IReadOnlyDictionary<GLAccountId, decimal> balances,
        IReadOnlyDictionary<GLAccountId, GLAccount> resolved)
    {
        var signedTotal = 0m;
        var byType = new Dictionary<GLAccountType, decimal>();
        var unclassified = 0;

        foreach (var (accountId, signed) in balances)
        {
            signedTotal += signed;
            if (resolved.TryGetValue(accountId, out var account))
            {
                byType[account.Type] = byType.GetValueOrDefault(account.Type) + signed;
            }
            else
            {
                unclassified++;
            }
        }

        var subtotals = byType
            .OrderBy(kv => kv.Key)
            .Select(kv => new AccountTypeSubtotal(kv.Key, kv.Value))
            .ToList();

        return new TrialBalanceResult(signedTotal, subtotals, unclassified);
    }

    private async Task<AgingDiffResult> ComputeArAgingAsync(
        ChartOfAccountsId chartId,
        DateOnly asOf,
        ArAgingSnapshot? snapshot,
        CancellationToken cancellationToken)
    {
        if (snapshot is null)
        {
            return AgingDiffResult.NotChecked;
        }

        var actual = await _arAging.GetAgingForChartAsync(chartId, asOf, cancellationToken).ConfigureAwait(false);
        var actualRows = actual.Rows.Select(r => (r.CustomerId, BucketIndex(r.Bucket), r.Balance));
        return DiffAging(actualRows, snapshot.Customers);
    }

    private async Task<AgingDiffResult> ComputeApAgingAsync(
        ChartOfAccountsId chartId,
        DateOnly asOf,
        ApAgingSnapshot? snapshot,
        CancellationToken cancellationToken)
    {
        if (snapshot is null)
        {
            return AgingDiffResult.NotChecked;
        }

        var actual = await _apAging.GetAgingForChartAsync(chartId, asOf, cancellationToken).ConfigureAwait(false);
        var actualRows = actual.Rows.Select(r => (r.VendorId, BucketIndex(r.Bucket), r.Balance));
        return DiffAging(actualRows, snapshot.Vendors);
    }

    /// <summary>
    /// Diff recomputed per-party bucket balances against a CO snapshot. Buckets are keyed 0–4
    /// (Current, 0-30, 31-60, 61-90, 90+); any party/bucket whose absolute diff exceeds $0.01 is
    /// emitted. Parties present on either side are considered (a party absent from one side
    /// contributes zero on that side).
    /// </summary>
    private static AgingDiffResult DiffAging(
        IEnumerable<(PartyId Party, int Bucket, decimal Balance)> actualRows,
        IReadOnlyList<PartyAgingSnapshotRow> expectedRows)
    {
        var actual = new Dictionary<PartyId, decimal[]>();
        foreach (var (party, bucket, balance) in actualRows)
        {
            if (!actual.TryGetValue(party, out var buckets))
            {
                buckets = new decimal[5];
                actual[party] = buckets;
            }
            buckets[bucket] += balance;
        }

        var expected = expectedRows.ToDictionary(
            r => r.PartyId,
            r => new[] { r.Current, r.Days0To30, r.Days31To60, r.Days61To90, r.Days90Plus });

        var parties = new HashSet<PartyId>(actual.Keys);
        parties.UnionWith(expected.Keys);

        var diffs = new List<PartyAgingDiff>();
        foreach (var party in parties)
        {
            var act = actual.GetValueOrDefault(party) ?? new decimal[5];
            var exp = expected.GetValueOrDefault(party) ?? new decimal[5];

            var bucketDiffs = new List<AgingBucketDiff>();
            for (var i = 0; i < 5; i++)
            {
                if (Math.Abs(act[i] - exp[i]) > MoneyTolerance)
                {
                    bucketDiffs.Add(new AgingBucketDiff(BucketLabels[i], exp[i], act[i]));
                }
            }

            if (bucketDiffs.Count > 0)
            {
                diffs.Add(new PartyAgingDiff(party, bucketDiffs));
            }
        }

        return new AgingDiffResult(Checked: true, diffs);
    }

    private static AccountBalanceDiffResult ComputeAccountBalanceDiff(
        IReadOnlyDictionary<GLAccountId, decimal> balances,
        IReadOnlyDictionary<GLAccountId, GLAccount> resolved,
        GlBalancesSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return AccountBalanceDiffResult.NotChecked;
        }

        // Actual balances keyed by account code (the cross-system join key). Accounts that could
        // not be resolved to a code cannot be matched against the snapshot and are skipped here;
        // they surface via the trial-balance UnclassifiedAccountCount instead.
        var actualByCode = new Dictionary<string, decimal>();
        foreach (var (accountId, signed) in balances)
        {
            if (resolved.TryGetValue(accountId, out var account))
            {
                actualByCode[account.Code] = actualByCode.GetValueOrDefault(account.Code) + signed;
            }
        }

        var expectedByCode = snapshot.Accounts.ToDictionary(a => a.AccountCode, a => a.SignedBalance);

        var codes = new HashSet<string>(actualByCode.Keys, StringComparer.Ordinal);
        codes.UnionWith(expectedByCode.Keys);

        var diffs = new List<AccountBalanceDiff>();
        foreach (var code in codes)
        {
            decimal? actual = actualByCode.TryGetValue(code, out var a) ? a : null;
            decimal? expected = expectedByCode.TryGetValue(code, out var e) ? e : null;
            if (Math.Abs((actual ?? 0m) - (expected ?? 0m)) > MoneyTolerance)
            {
                diffs.Add(new AccountBalanceDiff(code, expected, actual));
            }
        }

        diffs.Sort((x, y) => Math.Abs(y.Diff).CompareTo(Math.Abs(x.Diff)));
        return new AccountBalanceDiffResult(Checked: true, diffs);
    }

    private async Task<InvoiceBalanceCheckResult> ComputeInvoiceBalanceCheckAsync(
        TenantId tenantId,
        ChartOfAccountsId chartId,
        CancellationToken cancellationToken)
    {
        var invoices = await _invoices.ListByChartAsync(tenantId, chartId, cancellationToken).ConfigureAwait(false);

        var checked_ = 0;
        var discrepancies = new List<InvoiceBalanceDiscrepancy>();
        foreach (var invoice in invoices)
        {
            // Spec §4.6 step 5: only Issued / PartiallyPaid / Paid. Draft / Voided / WrittenOff are
            // out of scope (a write-off zeroes the balance via the BadDebt entry, not a cached diff).
            if (invoice.Status is not (InvoiceStatus.Issued or InvoiceStatus.PartiallyPaid or InvoiceStatus.Paid))
            {
                continue;
            }

            checked_++;
            if (invoice.Balance != invoice.Total - invoice.AmountPaid)
            {
                discrepancies.Add(new InvoiceBalanceDiscrepancy(
                    invoice.Id, invoice.InvoiceNumber, invoice.Total, invoice.AmountPaid, invoice.Balance));
            }
        }

        return new InvoiceBalanceCheckResult(checked_, discrepancies);
    }

    private static VerificationOutcome DetermineOutcome(
        TrialBalanceResult trialBalance,
        AgingDiffResult arAging,
        AgingDiffResult apAging,
        VerificationOptions options)
    {
        // Trial-balance mismatch is the most severe halt — check it first (spec §4.6 failure modes).
        if (!trialBalance.IsBalanced)
        {
            return VerificationOutcome.TrialBalanceMismatch;
        }

        if (!options.AllowAgingDrift && (!arAging.WithinThreshold || !apAging.WithinThreshold))
        {
            return VerificationOutcome.AgingReconciliationFailed;
        }

        return VerificationOutcome.Passed;
    }

    // ADR 0120 PR-E2: AgingBucket consolidated to Harborline.Api.Blocks.FinancialLedger.Models.AgingBucket.
    // AR and AP AgingRow.Bucket are both typed as the ledger AgingBucket — single overload suffices.
    private static int BucketIndex(AgingBucket bucket) => bucket switch
    {
        AgingBucket.Current    => 0,
        AgingBucket.Days0To30  => 1,
        AgingBucket.Days31To60 => 2,
        AgingBucket.Days61To90 => 3,
        AgingBucket.Days90Plus => 4,
        _ => 0,
    };
}
