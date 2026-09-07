using Harborline.Api.Foundation.Import.Outcomes;

namespace Harborline.Api.Blocks.FinancialLedger.Migration;

/// <summary>
/// Pass 1 (cost-center half) of the ERPNext → Harborline-native migration: idempotent
/// upsert of an ERPNext "Cost Center" into the §3.4 resolution
/// (<see cref="CostCenterResolution"/> — a known Property or a created
/// <see cref="Harborline.Api.Blocks.FinancialLedger.Models.Classification"/>). Returns the
/// canonical <c>Harborline.Api.Foundation.Import</c> <see cref="ImportOutcome{T}"/> DU so
/// the orchestrator records it into the shared <c>ImportCensus</c> (ADR 0100 C2).
/// </summary>
public interface IErpnextCostCenterImporter
{
    /// <summary>
    /// Resolve + upsert ONE cost-center. Idempotent on the cost-center
    /// <c>name</c>: a re-import at the same/older <c>modified</c> returns the
    /// <see cref="ImportOutcome{T}.Skipped"/> arm.
    /// </summary>
    Task<ImportOutcome<CostCenterResolution>> UpsertFromErpnextAsync(
        ErpnextCostCenterSource source,
        CancellationToken cancellationToken = default);
}
