using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Foundation.Import.Census;
using Harborline.Api.Foundation.Import.Outcomes;

namespace Harborline.Api.Blocks.FinancialLedger.Migration;

/// <summary>
/// The result of one Pass-3 opening-balance import
/// (<see cref="ErpnextOpeningBalancePass"/>). Carries the conserved
/// <see cref="ImportCensus"/> over the OPENING subset, the per-entry outcomes, the
/// count of non-opening entries the pass deferred (so the orchestrator's report
/// shows no record vanished from the full source set), and the opening
/// trial-balance aggregate Pass 6 reconciles (ADR 0100 C2). Content-free beyond the
/// resolved domain records the importer already produced — no raw source payload.
/// </summary>
/// <param name="Census">
/// The census over the imported opening-JE subset.
/// <see cref="ImportCensus.AssertConserved"/> has already passed (the pass throws
/// <see cref="ImportCensusViolationException"/> otherwise) — so this census's
/// <see cref="ImportCensus.Accounted"/> equals the opening-entry count.
/// </param>
/// <param name="Outcomes">
/// Per-opening-entry outcomes in source order — the orchestration audit trail. A
/// <see cref="ImportOutcome{T}.Rejected"/> entry here is an imbalanced opening JE,
/// an unresolved account, or a post-rejected entry.
/// </param>
/// <param name="NonOpeningCount">
/// The number of source entries that were NOT opening (deferred to Pass 4.4). Not
/// imported by this pass; reported so the full-source census (A7) reconciles.
/// </param>
/// <param name="OpeningDebitTotal">Σ of the debit columns of the opening entries that successfully landed a local record.</param>
/// <param name="OpeningCreditTotal">Σ of the credit columns of the opening entries that successfully landed a local record.</param>
public sealed record OpeningBalanceImportResult(
    ImportCensus Census,
    IReadOnlyList<ImportOutcome<JournalEntry>> Outcomes,
    int NonOpeningCount,
    decimal OpeningDebitTotal,
    decimal OpeningCreditTotal)
{
    /// <summary>The structured opening-entry rejects (imbalanced / unresolved-account / post-rejected) — the reject-bin slice for opening balances.</summary>
    public IReadOnlyList<ImportFailure> Rejects =>
        Outcomes
            .OfType<ImportOutcome<JournalEntry>.Rejected>()
            .Select(r => r.Failure)
            .ToList();

    /// <summary>
    /// The opening trial-balance net (<see cref="OpeningDebitTotal"/> -
    /// <see cref="OpeningCreditTotal"/>). Zero when the imported opening entries
    /// form a balanced opening trial balance. A non-zero net is surfaced for the
    /// Pass-6 report, NOT silently corrected (ADR 0100 C5).
    /// </summary>
    public decimal OpeningTrialBalanceNet => OpeningDebitTotal - OpeningCreditTotal;

    /// <summary>True iff the imported opening entries net to a zero opening trial balance.</summary>
    public bool OpeningTrialBalances => OpeningTrialBalanceNet == 0m;

    /// <summary>True iff every opening entry landed without a reject.</summary>
    public bool AllAccepted => Census.Rejected == 0;
}
