using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialPeriods.Models;
using Harborline.Api.Foundation.Import.Outcomes;

namespace Harborline.Api.Blocks.FinancialPeriods.Migration;

/// <summary>
/// ERPNext-side <c>Fiscal Year</c> import entry-point per Stage 02
/// §10.1. Idempotent on <see cref="ErpnextFiscalYearSource.Name"/> +
/// <see cref="ErpnextFiscalYearSource.Modified"/> — re-running the
/// importer on an unchanged source returns the
/// <see cref="ImportOutcome{T}.Skipped"/> arm.
/// </summary>
public interface IErpnextFiscalYearImporter
{
    /// <summary>
    /// Insert a new <see cref="FiscalYear"/> or update an existing one
    /// matched by external reference. Returns the
    /// <see cref="ImportOutcome{T}.Inserted"/> arm when a new row was created,
    /// <see cref="ImportOutcome{T}.Updated"/> when the existing row's
    /// version moved forward, and <see cref="ImportOutcome{T}.Skipped"/>
    /// otherwise (same / lower / unparseable version).
    /// </summary>
    /// <remarks>
    /// Closed local fiscal years are NEVER reopened by the importer —
    /// an ERPNext re-export with later <c>modified</c> on a Closed FY
    /// updates label/date fields but leaves
    /// <see cref="FiscalYear.Status"/> at <see cref="FiscalYearStatus.Closed"/>.
    /// Year-close is the <c>FiscalYearCloseService</c> path, not the
    /// importer's responsibility.
    /// </remarks>
    Task<ImportOutcome<FiscalYear>> UpsertFromErpnextAsync(
        ErpnextFiscalYearSource source,
        ChartOfAccountsId targetChart,
        CancellationToken cancellationToken = default);
}
