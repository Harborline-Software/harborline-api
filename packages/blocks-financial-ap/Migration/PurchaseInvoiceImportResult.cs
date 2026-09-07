using Harborline.Api.Blocks.FinancialAp.Models;
using Harborline.Api.Foundation.Import.Census;
using Harborline.Api.Foundation.Import.Outcomes;

namespace Harborline.Api.Blocks.FinancialAp.Migration;

/// <summary>
/// The result of one A4.2 purchase-invoice import pass
/// (<see cref="ErpnextPurchaseInvoicePass"/>). Carries the conserved
/// <see cref="ImportCensus"/> over the FULL source set and the per-bill outcomes
/// in source order — so the orchestrator's report shows no record vanished from
/// the source set (ADR 0100 C2).
///
/// <para>
/// Content-free beyond the resolved domain records the importer already
/// produced — no raw source payload, no PII (ADR 0100 C9).
/// </para>
/// </summary>
/// <param name="Census">
/// The census over the full purchase-invoice source set.
/// <see cref="ImportCensus.AssertConserved"/> has already passed (the pass throws
/// <see cref="ImportCensusViolationException"/> otherwise) — so this census's
/// <see cref="ImportCensus.Accounted"/> equals the source-record count.
/// </param>
/// <param name="Outcomes">
/// Per-bill outcomes in source order — the orchestration audit trail. A
/// <see cref="ImportOutcome{T}.Rejected"/> here is an unresolved supplier or a
/// missing/invalid required field; a <see cref="ImportOutcome{T}.Skipped"/> is an
/// already-imported (idempotent re-run) bill.
/// </param>
public sealed record PurchaseInvoiceImportResult(
    ImportCensus Census,
    IReadOnlyList<ImportOutcome<Bill>> Outcomes)
{
    /// <summary>The structured bill rejects — the reject-bin slice for purchase invoices (allowlisted; ADR 0100 C9).</summary>
    public IReadOnlyList<ImportFailure> Rejects =>
        Outcomes
            .OfType<ImportOutcome<Bill>.Rejected>()
            .Select(r => r.Failure)
            .ToList();

    /// <summary>The number of bills newly created (Inserted) by this run.</summary>
    public int Imported => Census.Inserted;

    /// <summary>True iff every source bill landed without a reject.</summary>
    public bool AllAccepted => Census.Rejected == 0;
}
