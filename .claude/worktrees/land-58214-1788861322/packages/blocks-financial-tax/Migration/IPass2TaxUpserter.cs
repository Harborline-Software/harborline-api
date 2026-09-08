using FL = Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialTax.Models;
using Harborline.Api.Foundation.Import.Outcomes;

namespace Harborline.Api.Blocks.FinancialTax.Migration;

/// <summary>
/// Pass-2 (reference-data) ERPNext tax upserter (ADR 0100 §4.2.3; importer
/// spec §4.2 sub-pass 2). Maps an ERPNext taxes-and-charges template to a
/// <see cref="TaxCode"/> plus a <see cref="TaxRate"/> child fan-out scoped to a
/// <see cref="TaxJurisdiction"/> synthesized from the template's
/// <c>Tax Category</c>. Returns the canonical
/// <see cref="ImportOutcome{T}"/> discriminated union from
/// <c>Harborline.Api.Foundation.Import</c> (Workstream A0) so each per-record outcome
/// records into an <see cref="Harborline.Api.Foundation.Import.Census.ImportCensus"/> —
/// the record-census-conservation contract (ADR 0100 C2).
/// </summary>
/// <remarks>
/// <para>
/// <b>Distinct from the legacy <c>ErpnextTaxImporter</c>.</b> That importer
/// returns the ledger-cluster legacy
/// <c>Harborline.Api.Blocks.FinancialLedger.Migration.ImportOutcome&lt;T&gt;</c> flat
/// record and reads the child rows from an inlined JSON string. This Pass-2
/// upserter is the canonical census-conserving surface the A6 reconcile + A7
/// orchestrator consume; the legacy importer is retained (shrink-only
/// allowlist) until its consumers migrate.
/// </para>
/// <para>
/// Tax records carry no PII. Effective-date defaults to <c>2000-01-01</c>
/// (covers history; user refines post-import — importer spec §4.2). A rate row
/// whose <c>account_head</c> does not resolve via <c>IAccountResolver</c> is
/// dropped (Pass 1 should have seeded it); a template with no resolvable rate
/// rows still imports the <see cref="TaxCode"/> (flagged in the reconcile
/// report — importer spec §4.2 failure modes).
/// </para>
/// </remarks>
public interface IPass2TaxUpserter
{
    /// <summary>
    /// Upsert a <see cref="TaxCode"/> + its <see cref="TaxRate"/> fan-out from
    /// an ERPNext template source. Idempotent on
    /// <see cref="ErpnextTaxTemplateSource.Name"/> via the
    /// <c>externalRef:&lt;name&gt;|modified:&lt;modified&gt;</c> marker on
    /// <see cref="TaxCode.Notes"/> (the cross-system FK until a dedicated
    /// <c>ExternalRef</c> field lands).
    /// </summary>
    /// <returns>
    /// <see cref="ImportOutcome{T}.Inserted"/> / <see cref="ImportOutcome{T}.Updated"/> /
    /// <see cref="ImportOutcome{T}.Skipped"/> on success; <see cref="ImportOutcome{T}.Rejected"/>
    /// (scalar-only <see cref="ImportFailure"/>) when a required field is missing.
    /// </returns>
    Task<ImportOutcome<TaxCode>> UpsertTaxTemplateAsync(
        ErpnextTaxTemplateSource source,
        FL.ChartOfAccountsId targetChart,
        CancellationToken cancellationToken = default);
}
