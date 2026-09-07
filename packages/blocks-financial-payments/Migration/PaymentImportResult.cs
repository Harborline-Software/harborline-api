using Harborline.Api.Blocks.FinancialPayments.Models;
using Harborline.Api.Foundation.Import.Census;
using Harborline.Api.Foundation.Import.Outcomes;

namespace Harborline.Api.Blocks.FinancialPayments.Migration;

/// <summary>
/// The result of one A4.3 payment-import pass (<see cref="ErpnextPaymentPass"/>).
/// Carries the conserved <see cref="ImportCensus"/> over the FULL source set, the
/// per-payment outcomes in source order, and the count of source records whose
/// party could not be resolved (rejected, never dropped) — so the orchestrator's
/// report shows no record vanished from the source set (ADR 0100 C2).
///
/// <para>
/// Content-free beyond the resolved domain records the importer already produced —
/// no raw source payload, no PII, no monetary amounts beyond what the
/// <see cref="Payment"/> records themselves carry (ADR 0100 C9).
/// </para>
/// </summary>
/// <param name="Census">
/// The census over the full payment source set.
/// <see cref="ImportCensus.AssertConserved"/> has already passed (the pass throws
/// <see cref="ImportCensusViolationException"/> otherwise) — so this census's
/// <see cref="ImportCensus.Accounted"/> equals the source-record count.
/// </param>
/// <param name="Outcomes">
/// Per-payment outcomes in source order — the orchestration audit trail. A
/// <see cref="ImportOutcome{T}.Rejected"/> here is an unresolved party, an
/// unknown payment_type, a non-USD currency, a non-positive amount, or an
/// out-of-range unallocated amount; a <see cref="ImportOutcome{T}.Skipped"/> is
/// an already-imported (idempotent re-run) payment.
/// </param>
public sealed record PaymentImportResult(
    ImportCensus Census,
    IReadOnlyList<ImportOutcome<Payment>> Outcomes)
{
    /// <summary>The structured payment rejects — the reject-bin slice for payments (allowlisted; ADR 0100 C9).</summary>
    public IReadOnlyList<ImportFailure> Rejects =>
        Outcomes
            .OfType<ImportOutcome<Payment>.Rejected>()
            .Select(r => r.Failure)
            .ToList();

    /// <summary>The number of payments newly created (Inserted) by this run.</summary>
    public int Imported => Census.Inserted;

    /// <summary>True iff every source payment landed without a reject.</summary>
    public bool AllAccepted => Census.Rejected == 0;
}
