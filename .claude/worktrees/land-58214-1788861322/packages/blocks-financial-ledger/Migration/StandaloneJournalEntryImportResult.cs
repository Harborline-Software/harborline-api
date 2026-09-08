using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Foundation.Import.Census;
using Harborline.Api.Foundation.Import.Outcomes;

namespace Harborline.Api.Blocks.FinancialLedger.Migration;

/// <summary>
/// The result of one Pass-4.4 standalone-journal-entry import
/// (<see cref="ErpnextStandaloneJournalEntryPass"/>). Carries the conserved
/// <see cref="ImportCensus"/> over the SUBMITTED-STANDALONE subset, the per-entry
/// outcomes, the count of opening entries the pass deferred to Pass 3, and the count
/// of non-submitted standalone entries it partitioned out — so the orchestrator's
/// report shows no record vanished from the full source set (ADR 0100 C2).
/// Content-free beyond the resolved domain records the importer already produced — no
/// raw source payload (ADR 0100 C9).
/// </summary>
/// <param name="Census">
/// The census over the imported submitted-standalone-JE subset.
/// <see cref="ImportCensus.AssertConserved"/> has already passed (the pass throws
/// <see cref="ImportCensusViolationException"/> otherwise) — so this census's
/// <see cref="ImportCensus.Accounted"/> equals the submitted-standalone-entry count.
/// </param>
/// <param name="Outcomes">
/// Per-submitted-standalone-entry outcomes in source order — the orchestration audit
/// trail. A <see cref="ImportOutcome{T}.Rejected"/> entry here is an imbalanced JE, an
/// unresolved account, or a post-rejected entry; a <see cref="ImportOutcome{T}.Skipped"/>
/// is an already-posted (idempotent re-import) entry.
/// </param>
/// <param name="OpeningCount">
/// The number of source entries that were OPENING (deferred to Pass 3). Not imported
/// by this pass; reported so the full-source census (A7) reconciles.
/// </param>
/// <param name="NonSubmittedCount">
/// The number of NON-OPENING source entries with <c>docstatus != 1</c> (Draft or
/// Cancelled). Out of this pass's import scope (the reconciliation gate counts
/// <c>docstatus==1</c> only); counted, not dropped (ADR 0100 C2).
/// </param>
public sealed record StandaloneJournalEntryImportResult(
    ImportCensus Census,
    IReadOnlyList<ImportOutcome<JournalEntry>> Outcomes,
    int OpeningCount,
    int NonSubmittedCount)
{
    /// <summary>The structured standalone-entry rejects (imbalanced / unresolved-account / post-rejected) — the reject-bin slice for standalone JEs.</summary>
    public IReadOnlyList<ImportFailure> Rejects =>
        Outcomes
            .OfType<ImportOutcome<JournalEntry>.Rejected>()
            .Select(r => r.Failure)
            .ToList();

    /// <summary>The number of submitted-standalone entries newly posted (Inserted) by this run.</summary>
    public int Posted => Census.Inserted;

    /// <summary>True iff every submitted-standalone entry landed without a reject.</summary>
    public bool AllAccepted => Census.Rejected == 0;
}
