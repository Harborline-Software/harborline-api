using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialPeriods.Models;
using Harborline.Api.Foundation.Import.Outcomes;

namespace Harborline.Api.Blocks.FinancialPeriods.Migration;

/// <summary>
/// Pass-2 (reference-data) ERPNext fiscal-year + fiscal-period upserter
/// (ADR 0100 §4.2.3; importer spec §4.2 sub-pass 1). Returns the canonical
/// <see cref="ImportOutcome{T}"/> discriminated union from
/// <c>Harborline.Api.Foundation.Import</c> (Workstream A0) so each per-record outcome
/// records into an <see cref="Harborline.Api.Foundation.Import.Census.ImportCensus"/> —
/// the record-census-conservation contract (ADR 0100 C2).
/// </summary>
/// <remarks>
/// <para>
/// <b>Distinct from the legacy importers.</b> <c>ErpnextFiscalYearImporter</c> +
/// <c>ErpnextFiscalPeriodImporter</c> return the ledger-cluster legacy
/// <c>Harborline.Api.Blocks.FinancialLedger.Migration.ImportOutcome&lt;T&gt;</c> flat
/// record. This Pass-2 upserter is the canonical census-conserving surface the
/// A6 reconcile + A7 orchestrator consume; the legacy importers are retained
/// (shrink-only allowlist) until their consumers migrate.
/// </para>
/// <para>
/// ERPNext exports <c>Fiscal Year</c> as a discrete DocType but does NOT export
/// <c>FiscalPeriod</c> — it synthesizes monthly buckets at query time. So the
/// period sub-pass <b>synthesizes</b> the period set from an already-imported
/// <see cref="FiscalYear"/> (importer spec §4.2 sub-pass 1; periods all
/// <see cref="FiscalPeriodStatus.Open"/> — close is a post-import user action).
/// </para>
/// </remarks>
public interface IPass2PeriodUpserter
{
    /// <summary>
    /// Upsert an ERPNext <c>Fiscal Year</c> into the canonical
    /// <see cref="FiscalYear"/> substrate. Idempotent on
    /// <see cref="ErpnextFiscalYearSource.Name"/> via the stored
    /// <see cref="FiscalYear.ExternalModifiedAtUtc"/> version stamp.
    /// </summary>
    /// <returns>
    /// <see cref="ImportOutcome{T}.Inserted"/> on a new FY,
    /// <see cref="ImportOutcome{T}.Updated"/> when the version advanced,
    /// <see cref="ImportOutcome{T}.Skipped"/> when unchanged/older, and
    /// <see cref="ImportOutcome{T}.Rejected"/> (scalar-only
    /// <see cref="ImportFailure"/>) on an unparseable <c>modified</c> stamp or a
    /// structurally invalid FY (start &gt; end, empty label).
    /// </returns>
    Task<ImportOutcome<FiscalYear>> UpsertFiscalYearAsync(
        ErpnextFiscalYearSource source,
        ChartOfAccountsId targetChart,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Synthesize the <see cref="FiscalPeriod"/> rows for a previously-imported
    /// <see cref="FiscalYear"/>. Idempotent per-FY: a re-run on a FY whose
    /// periods already exist returns <see cref="ImportOutcome{T}.Skipped"/> for
    /// every period; the first run returns
    /// <see cref="ImportOutcome{T}.Inserted"/>. Returns
    /// <see cref="ImportOutcome{T}.Rejected"/> per-period when the synthesized
    /// set fails the contiguity validator, and an empty list when the FY id is
    /// unknown (the caller records that as a halt / unresolved reference).
    /// </summary>
    Task<IReadOnlyList<ImportOutcome<FiscalPeriod>>> SynthesizePeriodsAsync(
        FiscalYearId fiscalYearId,
        FiscalPeriodKind kind = FiscalPeriodKind.Monthly,
        CancellationToken cancellationToken = default);
}
