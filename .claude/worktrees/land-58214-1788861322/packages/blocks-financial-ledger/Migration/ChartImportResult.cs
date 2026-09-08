using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Foundation.Import.Census;
using Harborline.Api.Foundation.Import.Outcomes;

namespace Harborline.Api.Blocks.FinancialLedger.Migration;

/// <summary>
/// The result of one Pass-1 chart-of-accounts import
/// (<see cref="ErpnextChartImportPass"/>). Carries the conserved
/// <see cref="ImportCensus"/>, the per-account outcomes in topological order, the
/// cost-center resolutions, and the structured reject list (ADR 0100 C2 — "no
/// financial record vanishes without a report line"). Content-free beyond the
/// resolved domain records the upserters already produced — no raw source payload.
/// </summary>
/// <param name="AccountCensus">
/// The census over the Account DocType. <see cref="ImportCensus.AssertConserved"/>
/// has already passed (the pass throws <see cref="ImportCensusViolationException"/>
/// otherwise) — so this census's <see cref="ImportCensus.Accounted"/> equals the
/// source account count.
/// </param>
/// <param name="CostCenterCensus">The census over the Cost Center DocType (same conservation guarantee).</param>
/// <param name="AccountOutcomes">
/// Per-account outcomes in the parent-first topological order the pass applied —
/// the orchestration audit trail. A <see cref="ImportOutcome{T}.Rejected"/> entry
/// here is a cycle-participant (the only account-level reject this pass produces).
/// </param>
/// <param name="CostCenterOutcomes">Per-cost-center outcomes (Property resolution or created Classification).</param>
public sealed record ChartImportResult(
    ImportCensus AccountCensus,
    ImportCensus CostCenterCensus,
    IReadOnlyList<ImportOutcome<GLAccount>> AccountOutcomes,
    IReadOnlyList<ImportOutcome<CostCenterResolution>> CostCenterOutcomes)
{
    /// <summary>The structured account-level rejects (cycle participants) — the reject-bin slice for accounts.</summary>
    public IReadOnlyList<ImportFailure> AccountRejects =>
        AccountOutcomes
            .OfType<ImportOutcome<GLAccount>.Rejected>()
            .Select(r => r.Failure)
            .ToList();

    /// <summary>The structured cost-center-level rejects — the reject-bin slice for cost-centers.</summary>
    public IReadOnlyList<ImportFailure> CostCenterRejects =>
        CostCenterOutcomes
            .OfType<ImportOutcome<CostCenterResolution>.Rejected>()
            .Select(r => r.Failure)
            .ToList();

    /// <summary>True iff every account AND cost-center landed without a reject.</summary>
    public bool AllAccepted => AccountCensus.Rejected == 0 && CostCenterCensus.Rejected == 0;
}
