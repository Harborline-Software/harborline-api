using Harborline.Api.Blocks.FinancialLedger.Migration;
using Harborline.Api.Blocks.Migration.Erpnext.Extraction;
using Harborline.Api.Blocks.Migration.Erpnext.Reconciliation;
using Harborline.Api.Blocks.Migration.Erpnext.Verification;
using Harborline.Api.Foundation.Import.Outcomes;

namespace Harborline.Api.Blocks.Migration.Erpnext.Reporting;

/// <summary>
/// The complete, already-gathered input to <see cref="MigrationReportRenderer.Render"/> —
/// the pure aggregate the orchestrator (A7) assembles after all six passes complete, and
/// the renderer turns into <c>migration-report.md</c> (spec §4.6 step 6).
/// </summary>
/// <remarks>
/// <para>
/// Pass 6 owns the <i>shape</i> of this aggregate and the pure renderer over it; A7 owns
/// <i>populating</i> it (threading the reject-bin rows, the per-DocType census, the pass
/// durations, the cost-center resolutions, and the warning counts gathered across passes 1-5)
/// and writing the rendered markdown to <c>&lt;export-root&gt;/migration-report.md</c>. Keeping
/// the aggregate a plain immutable record leaves the renderer a deterministic pure function,
/// unit-testable without disk, a DB, or a live run.
/// </para>
/// <para>
/// Every collection is required (use an empty list, never <see langword="null"/>, when a section
/// has no rows) so the renderer never has to null-check — a section with no rows still renders its
/// heading plus a "none" line, which is the audit-visible signal that the section was considered.
/// </para>
/// </remarks>
/// <param name="RunSummary">Run provenance + per-DocType census + pass durations (report section 1).</param>
/// <param name="Verification">The Pass 6 verification result — trial balance, AR/AP aging, per-account + invoice balances (report sections 2-5).</param>
/// <param name="Reconciliation">The Pass 5 reconciliation result — drives the unapplied-payment list (report section 7).</param>
/// <param name="RejectBin">Every rejected record's allowlisted projection, across all passes (report section 6).</param>
/// <param name="CostCenterResolutions">Each cost-center's §3.4 resolution outcome (report section 8).</param>
/// <param name="Warnings">Non-fatal advisories gathered across the run — unknown voucher types, unmapped DocTypes, the null-currency-assumed-USD count, etc. (report section 9).</param>
public sealed record MigrationReportInput(
    RunSummary RunSummary,
    VerificationResult Verification,
    ReconciliationPassResult Reconciliation,
    IReadOnlyList<ImportFailure> RejectBin,
    IReadOnlyList<CostCenterResolution> CostCenterResolutions,
    IReadOnlyList<ReportWarning> Warnings);

/// <summary>
/// Run provenance for the report's "Run summary" section (spec §4.6 step 6): the run id, the
/// moment the report was generated, the source-access mode + per-DocType census, and the
/// per-pass wall-clock durations.
/// </summary>
/// <param name="RunId">The opaque run identifier (safe to render; not PII — ADR 0100 C9).</param>
/// <param name="GeneratedAt">When the report was generated (rendered in invariant ISO-8601 for determinism).</param>
/// <param name="SourceInventory">The source DocType census + access mode (record counts per DocType).</param>
/// <param name="PassDurations">One entry per pass, in execution order.</param>
public sealed record RunSummary(
    string RunId,
    DateTimeOffset GeneratedAt,
    ErpnextSourceInventory SourceInventory,
    IReadOnlyList<PassDuration> PassDurations);

/// <summary>One pass's wall-clock duration, for the run-summary timing table.</summary>
/// <param name="PassName">Human-readable pass label (e.g. <c>"Pass 4 — Transactional history"</c>).</param>
/// <param name="Duration">How long the pass took.</param>
public sealed record PassDuration(string PassName, TimeSpan Duration);

/// <summary>
/// A bounded, machine-readable code for a non-fatal migration-report warning (spec §4.6 step 6,
/// "Any warnings" section). Mirrors the <see cref="ImportRejectReason"/> boundedness discipline so
/// the report's warning taxonomy stays stable across runs.
/// </summary>
public enum ReportWarningCode
{
    /// <summary>
    /// Records whose source currency field was null/empty were assumed to be the chart's base
    /// currency (USD) rather than rejected. The count makes the silent assumption auditable
    /// (.NET-architect finding F2, carried from earlier repository ticket #205). Distinct from a non-USD currency,
    /// which is a hard reject (<see cref="ImportRejectReason.UnsupportedCurrency"/>).
    /// </summary>
    NullCurrencyAssumedUsd,

    /// <summary>An ERPNext <c>voucher_type</c> / <c>doctype</c> value was not recognized by the v15 map.</summary>
    UnknownVoucherType,

    /// <summary>Business/financial-looking DocTypes were present in the source but not mapped (the <c>_unmapped/</c> census).</summary>
    UnmappedDocType,

    /// <summary>A warning that does not fit a well-known code; <see cref="ReportWarning.Description"/> carries the detail.</summary>
    Other,
}

/// <summary>
/// One non-fatal advisory line for the report's warnings section. Content-free beyond a bounded
/// code, a human-readable description, and an affected-record count (ADR 0100 C9: no PII, no
/// monetary amounts, no record contents — descriptions are descriptors, not value holders).
/// </summary>
/// <param name="Code">The bounded warning category.</param>
/// <param name="Description">A human-readable description — must not contain record contents, PII, or monetary amounts.</param>
/// <param name="Count">The number of records/files this warning covers; <c>0</c> when the warning is not a count.</param>
public sealed record ReportWarning(ReportWarningCode Code, string Description, int Count)
{
    /// <summary>
    /// The null-currency-assumed-USD advisory (finding F2): <paramref name="count"/> records had no
    /// currency set and were treated as the chart's USD base currency rather than rejected.
    /// </summary>
    public static ReportWarning NullCurrencyAssumedUsd(int count) =>
        new(ReportWarningCode.NullCurrencyAssumedUsd,
            "Records with no currency set were assumed to be the chart base currency (USD).",
            count);

    /// <summary>An unknown-voucher-type advisory covering <paramref name="count"/> records.</summary>
    public static ReportWarning UnknownVoucherType(int count) =>
        new(ReportWarningCode.UnknownVoucherType,
            "Source records carried a voucher_type not recognized by the ERPNext v15 map.",
            count);

    /// <summary>
    /// An unmapped-DocType advisory: <paramref name="count"/> business/financial-looking DocTypes
    /// were present in the source but not mapped (listed in the <c>_unmapped/</c> census).
    /// </summary>
    public static ReportWarning UnmappedDocType(int count) =>
        new(ReportWarningCode.UnmappedDocType,
            "Business/financial-looking DocTypes were present in the source but not mapped (see _unmapped/).",
            count);
}
