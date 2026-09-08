using System.Globalization;
using System.Text;
using Harborline.Api.Blocks.FinancialLedger.Migration;
using Harborline.Api.Blocks.Migration.Erpnext.Extraction;
using Harborline.Api.Blocks.Migration.Erpnext.Reconciliation;
using Harborline.Api.Blocks.Migration.Erpnext.Verification;

namespace Harborline.Api.Blocks.Migration.Erpnext.Reporting;

/// <summary>
/// Renders a <see cref="MigrationReportInput"/> into the <c>migration-report.md</c> markdown
/// document (spec §4.6 step 6). A pure, deterministic, side-effect-free function: same input
/// always yields the same byte-identical markdown — no clock reads, no filesystem, no culture
/// drift (all numbers + dates use <see cref="CultureInfo.InvariantCulture"/>). The orchestrator
/// (A7) owns writing the result to <c>&lt;export-root&gt;/migration-report.md</c>.
/// </summary>
/// <remarks>
/// The report is the verification artifact handed to the CO, not a log — so unlike the audit
/// surface it legitimately carries monetary diffs + balances + opaque ids (party/invoice/payment
/// ids, account codes). It still carries no PII: the diff rows are keyed by opaque identifiers and
/// human-readable account codes, never party names / emails / phones (ADR 0100 C9).
/// </remarks>
public static class MigrationReportRenderer
{
    private const string ThresholdNote = "$0.01 per-row threshold";

    /// <summary>
    /// Renders the full eight-section migration report (spec §4.6 step 6) as a markdown string.
    /// </summary>
    /// <param name="input">The gathered report input. Every collection must be non-null (empty, not null, when a section has no rows).</param>
    /// <returns>The complete <c>migration-report.md</c> contents.</returns>
    public static string Render(MigrationReportInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var sb = new StringBuilder();
        sb.AppendLine("# ERPNext → Harborline Migration Report");
        sb.AppendLine();
        sb.AppendLine($"**Overall result:** {OutcomeLabel(input.Verification.Outcome)}");
        sb.AppendLine();

        RenderRunSummary(sb, input.RunSummary);
        RenderTrialBalance(sb, input.Verification);
        RenderAging(sb, "3. AR Aging Diff", input.Verification.ArAging, "ar-aging-snapshot.json");
        RenderAging(sb, "4. AP Aging Diff", input.Verification.ApAging, "ap-aging-snapshot.json");
        RenderAccountBalances(sb, input.Verification.AccountBalances);
        RenderRejectBin(sb, input.RejectBin);
        RenderUnappliedPayments(sb, input.Reconciliation);
        RenderCostCenters(sb, input.CostCenterResolutions);
        RenderWarnings(sb, input.Warnings);

        return sb.ToString();
    }

    private static void RenderRunSummary(StringBuilder sb, RunSummary run)
    {
        sb.AppendLine("## 1. Run Summary");
        sb.AppendLine();
        sb.AppendLine($"- Run ID: `{run.RunId}`");
        sb.AppendLine($"- Generated: {run.GeneratedAt.UtcDateTime.ToString("u", CultureInfo.InvariantCulture)}");
        sb.AppendLine($"- Source access mode: {run.SourceInventory.SourceMode}");
        sb.AppendLine();

        sb.AppendLine("### Pass durations");
        sb.AppendLine();
        if (run.PassDurations.Count == 0)
        {
            sb.AppendLine("_No pass timings recorded._");
        }
        else
        {
            sb.AppendLine("| Pass | Duration |");
            sb.AppendLine("|---|---|");
            foreach (var pass in run.PassDurations)
            {
                sb.AppendLine($"| {pass.PassName} | {FormatDuration(pass.Duration)} |");
            }
        }
        sb.AppendLine();

        sb.AppendLine("### DocType census");
        sb.AppendLine();
        var entries = run.SourceInventory.Entries
            .OrderBy(e => e.Classification)
            .ThenBy(e => e.DocType, StringComparer.Ordinal)
            .ToList();
        if (entries.Count == 0)
        {
            sb.AppendLine("_No DocTypes found in the source._");
        }
        else
        {
            sb.AppendLine("| DocType | Classification | Source rows |");
            sb.AppendLine("|---|---|---|");
            foreach (var entry in entries)
            {
                sb.AppendLine($"| {entry.DocType} | {entry.Classification} | {entry.SourceRowCount.ToString(CultureInfo.InvariantCulture)} |");
            }
        }
        sb.AppendLine();
        sb.AppendLine($"- Mapped DocTypes: {run.SourceInventory.Mapped.Count().ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"- Known-irrelevant DocTypes: {run.SourceInventory.KnownIrrelevant.Count().ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"- Unmapped-unknown DocTypes (see `_unmapped/`): {run.SourceInventory.UnmappedUnknownCount.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine();
    }

    private static void RenderTrialBalance(StringBuilder sb, VerificationResult verification)
    {
        var tb = verification.TrialBalance;
        sb.AppendLine("## 2. Trial Balance");
        sb.AppendLine();
        if (tb.IsBalanced)
        {
            sb.AppendLine($"**BALANCED** — signed total {Money(tb.SignedTotal)} (spec mandates exact $0).");
        }
        else
        {
            sb.AppendLine($"**MISMATCH** — signed total {Money(tb.SignedTotal)} (expected exactly $0).");
            sb.AppendLine();
            sb.AppendLine("> Hard halt: the importer rolls back the entire run (spec §4.6 failure mode 1).");
        }
        sb.AppendLine();

        sb.AppendLine("| Account type | Signed subtotal |");
        sb.AppendLine("|---|---|");
        foreach (var subtotal in tb.Subtotals)
        {
            sb.AppendLine($"| {subtotal.Type} | {Money(subtotal.SignedTotal)} |");
        }
        sb.AppendLine();
        if (tb.UnclassifiedAccountCount > 0)
        {
            sb.AppendLine($"- Unclassified accounts (type unresolved; balances still counted): {tb.UnclassifiedAccountCount.ToString(CultureInfo.InvariantCulture)}");
            sb.AppendLine();
        }

        var inv = verification.InvoiceBalances;
        sb.AppendLine("### Invoice balance reconciliation");
        sb.AppendLine();
        sb.AppendLine($"Invoices checked (Issued / PartiallyPaid / Paid): {inv.InvoicesChecked.ToString(CultureInfo.InvariantCulture)}.");
        sb.AppendLine();
        if (inv.Discrepancies.Count == 0)
        {
            sb.AppendLine("Every cached balance equals `Total − AmountPaid`.");
        }
        else
        {
            sb.AppendLine("| Invoice | Total | Amount paid | Recorded balance | Expected balance |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var d in inv.Discrepancies.OrderBy(d => d.InvoiceNumber, StringComparer.Ordinal))
            {
                sb.AppendLine($"| {d.InvoiceNumber} | {Money(d.Total)} | {Money(d.AmountPaid)} | {Money(d.RecordedBalance)} | {Money(d.ExpectedBalance)} |");
            }
        }
        sb.AppendLine();
    }

    private static void RenderAging(StringBuilder sb, string heading, AgingDiffResult aging, string snapshotFile)
    {
        sb.AppendLine($"## {heading}");
        sb.AppendLine();
        if (!aging.Checked)
        {
            sb.AppendLine($"_Not checked — no `{snapshotFile}` supplied in the export root._");
            sb.AppendLine();
            return;
        }
        if (aging.WithinThreshold)
        {
            sb.AppendLine($"Within {ThresholdNote} for every party/bucket.");
            sb.AppendLine();
            return;
        }

        sb.AppendLine($"Diffs exceeding {ThresholdNote}:");
        sb.AppendLine();
        sb.AppendLine("| Party | Bucket | Expected | Actual | Diff |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var party in aging.ExceedingRows.OrderBy(p => p.PartyId.Value, StringComparer.Ordinal))
        {
            foreach (var bucket in party.Buckets)
            {
                sb.AppendLine($"| {party.PartyId.Value} | {bucket.Bucket} | {Money(bucket.Expected)} | {Money(bucket.Actual)} | {Money(bucket.Diff)} |");
            }
        }
        sb.AppendLine();
    }

    private static void RenderAccountBalances(StringBuilder sb, AccountBalanceDiffResult balances)
    {
        sb.AppendLine("## 5. Per-Account Balance Diff");
        sb.AppendLine();
        if (!balances.Checked)
        {
            sb.AppendLine("_Not checked — no `gl-balances-snapshot.json` supplied in the export root._");
            sb.AppendLine();
            return;
        }
        if (balances.WithinThreshold)
        {
            sb.AppendLine($"Within {ThresholdNote} for every account.");
            sb.AppendLine();
            return;
        }

        sb.AppendLine($"Diffs exceeding {ThresholdNote}, sorted by absolute difference:");
        sb.AppendLine();
        sb.AppendLine("| Account code | Expected | Actual | Diff |");
        sb.AppendLine("|---|---|---|---|");
        var sorted = balances.Diffs
            .OrderByDescending(d => Math.Abs(d.Diff))
            .ThenBy(d => d.AccountCode, StringComparer.Ordinal);
        foreach (var d in sorted)
        {
            sb.AppendLine($"| {d.AccountCode} | {MoneyOrAbsent(d.Expected)} | {MoneyOrAbsent(d.Actual)} | {Money(d.Diff)} |");
        }
        sb.AppendLine();
    }

    private static void RenderRejectBin(StringBuilder sb, IReadOnlyList<Harborline.Api.Foundation.Import.Outcomes.ImportFailure> rejectBin)
    {
        sb.AppendLine("## 6. Reject-Bin Summary");
        sb.AppendLine();
        if (rejectBin.Count == 0)
        {
            sb.AppendLine("No records were rejected.");
            sb.AppendLine();
            return;
        }

        var byReason = rejectBin
            .GroupBy(r => r.ReasonCode)
            .OrderBy(g => g.Key, StringComparer.Ordinal);
        sb.AppendLine("| Reject reason | Count |");
        sb.AppendLine("|---|---|");
        foreach (var group in byReason)
        {
            sb.AppendLine($"| {group.Key} | {group.Count().ToString(CultureInfo.InvariantCulture)} |");
        }
        sb.AppendLine();
        sb.AppendLine($"Total rejected: {rejectBin.Count.ToString(CultureInfo.InvariantCulture)}.");
        sb.AppendLine();
    }

    private static void RenderUnappliedPayments(StringBuilder sb, ReconciliationPassResult reconciliation)
    {
        sb.AppendLine("## 7. Unapplied Payments");
        sb.AppendLine();
        sb.AppendLine(
            $"Pass 5 processed {reconciliation.TotalProcessed.ToString(CultureInfo.InvariantCulture)} unapplied payments: " +
            $"{reconciliation.AppliedCount.ToString(CultureInfo.InvariantCulture)} applied, " +
            $"{reconciliation.AmbiguousCount.ToString(CultureInfo.InvariantCulture)} ambiguous, " +
            $"{reconciliation.UnmatchedCount.ToString(CultureInfo.InvariantCulture)} unmatched.");
        sb.AppendLine();

        var remaining = reconciliation.Outcomes
            .Where(o => o.Kind != PaymentReconciliationOutcomeKind.Applied)
            .OrderBy(o => o.PaymentId.Value, StringComparer.Ordinal)
            .ToList();
        if (remaining.Count == 0)
        {
            sb.AppendLine("All payments were applied to exactly one target — none left unapplied.");
            sb.AppendLine();
            return;
        }

        sb.AppendLine("Payments left unapplied (need CO resolution):");
        sb.AppendLine();
        sb.AppendLine("| Payment | Outcome | Candidate targets |");
        sb.AppendLine("|---|---|---|");
        foreach (var o in remaining)
        {
            var candidates = o.AmbiguousCandidateIds is { Count: > 0 }
                ? string.Join(", ", o.AmbiguousCandidateIds)
                : "—";
            sb.AppendLine($"| {o.PaymentId.Value} | {o.Kind} | {candidates} |");
        }
        sb.AppendLine();
    }

    private static void RenderCostCenters(StringBuilder sb, IReadOnlyList<CostCenterResolution> resolutions)
    {
        sb.AppendLine("## 8. Cost-Center Resolution");
        sb.AppendLine();
        var toProperty = resolutions.Count(r => r.Kind == CostCenterResolutionKind.ResolvedToProperty);
        var toClassification = resolutions.Count(r => r.Kind == CostCenterResolutionKind.CreatedClassification);
        sb.AppendLine(
            $"{resolutions.Count.ToString(CultureInfo.InvariantCulture)} cost-centers resolved: " +
            $"{toProperty.ToString(CultureInfo.InvariantCulture)} → existing property, " +
            $"{toClassification.ToString(CultureInfo.InvariantCulture)} → new classification.");
        sb.AppendLine();
        if (resolutions.Count == 0)
        {
            sb.AppendLine("No cost-centers were present in the source.");
            sb.AppendLine();
            return;
        }

        sb.AppendLine("| Cost-center (externalRef) | Resolution |");
        sb.AppendLine("|---|---|");
        foreach (var r in resolutions.OrderBy(r => r.ExternalRef, StringComparer.Ordinal))
        {
            var target = r.Kind == CostCenterResolutionKind.ResolvedToProperty
                ? $"Property `{r.PropertyId?.Value}`"
                : $"Classification `{r.Classification?.Id.Value}` (created)";
            sb.AppendLine($"| {r.ExternalRef} | {target} |");
        }
        sb.AppendLine();
    }

    private static void RenderWarnings(StringBuilder sb, IReadOnlyList<ReportWarning> warnings)
    {
        sb.AppendLine("## 9. Warnings");
        sb.AppendLine();
        if (warnings.Count == 0)
        {
            sb.AppendLine("No warnings.");
            sb.AppendLine();
            return;
        }

        foreach (var w in warnings)
        {
            var count = w.Count > 0 ? $" ({w.Count.ToString(CultureInfo.InvariantCulture)})" : string.Empty;
            sb.AppendLine($"- **{w.Code}**{count}: {w.Description}");
        }
        sb.AppendLine();
    }

    private static string OutcomeLabel(VerificationOutcome outcome) => outcome switch
    {
        VerificationOutcome.Passed => "PASSED",
        VerificationOutcome.TrialBalanceMismatch => "FAILED — trial balance mismatch (run rolled back)",
        VerificationOutcome.AgingReconciliationFailed => "FAILED — aging reconciliation exceeded threshold",
        _ => outcome.ToString(),
    };

    private static string Money(decimal amount) =>
        "$" + amount.ToString("0.00", CultureInfo.InvariantCulture);

    private static string MoneyOrAbsent(decimal? amount) =>
        amount is null ? "—" : Money(amount.Value);

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture) + "s";
}
