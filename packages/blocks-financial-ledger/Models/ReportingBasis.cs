namespace Harborline.Api.Blocks.FinancialLedger.Models;

/// <summary>
/// The accounting basis a consolidation / cartridge read is presented on
/// (ADR 0105 §5, ruling #4). Basis is a <b>reporting-time presentation filter</b>,
/// NOT a parallel ledger — the books stay accrual; a report can present a
/// cash-basis view by changing which postings count at the projection read.
/// </summary>
/// <remarks>
/// <b>One basis per scope (financial F-5, load-bearing).</b>
/// <see cref="Services.IConsolidationService.ConsolidateAsync"/> takes a single
/// <see cref="ReportingBasis"/> for the WHOLE scope; combining a cash-basis entity
/// with an accrual-basis entity into one rollup is meaningless and is rejected,
/// never silently summed. Because the parameter is scope-level (not per-entity),
/// a mixed-basis total cannot be expressed.
///
/// <para>
/// <b>v1 ships <see cref="Accrual"/> only (ADR 0105 §5.1 disclosed limitation).</b>
/// <see cref="Cash"/> is a named Wave-3 build — it needs the payment-clearing
/// linkage to decide whether a posting is cash-realized. Until then a
/// <see cref="Cash"/> request is rejected loudly (the gap is disclosed, not
/// implied parity): a cash-basis filer needs a CPA-side accrual-to-cash
/// adjustment because Harborline's accrual statements are not the as-filed numbers.
/// </para>
/// </remarks>
public enum ReportingBasis
{
    /// <summary>Accrual basis — the v1 default and only supported basis.</summary>
    Accrual,

    /// <summary>
    /// Cash basis — postings whose settlement is realized via a cleared payment.
    /// Reserved here so the substrate is shaped to accept it; the posting-selection
    /// internals are a Wave-3 build (ADR 0105 §5). Rejected with
    /// <see cref="System.NotSupportedException"/> in v1.
    /// </summary>
    Cash,
}
