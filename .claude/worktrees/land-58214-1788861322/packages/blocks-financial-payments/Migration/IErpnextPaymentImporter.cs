using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialPayments.Models;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Import.Outcomes;

namespace Harborline.Api.Blocks.FinancialPayments.Migration;

/// <summary>
/// A4.3 of the ERPNext → Harborline-native migration: idempotent upsert of a
/// <see cref="Payment"/> from an <see cref="ErpnextPaymentSource"/> (ERPNext
/// <c>Payment Entry</c>). Per-record upserter — the orchestration pass
/// (<see cref="ErpnextPaymentPass"/>) drives this over a source set.
///
/// <para>
/// Returns the canonical <c>Harborline.Api.Foundation.Import</c>
/// <see cref="ImportOutcome{T}"/> DU (ADR 0100 C2 / OQ-A): a record that cannot
/// be imported (unknown payment_type, non-USD currency, non-positive amount,
/// out-of-range unallocated amount) returns the
/// <see cref="ImportOutcome{T}.Rejected"/> arm carrying a structured,
/// allowlisted <see cref="ImportFailure"/> — NEVER a thrown exception that
/// aborts the pass, and NEVER a silent drop (C2/C5).
/// </para>
///
/// <para>
/// <b>Idempotency (ADR 0100 C1 / C7).</b> Dedupes on the ERPNext
/// <c>name</c> via <c>Payment.ExternalRef</c>. Re-import of the same source at
/// the same/older <c>Modified</c> stamp returns <see cref="ImportOutcome{T}.Skipped"/>
/// (count-stable); a strictly-newer stamp returns <see cref="ImportOutcome{T}.Updated"/>.
/// The version stamp lives in the indexed <see cref="Payment.ExternalRefVersion"/>
/// field, never in <see cref="Payment.Notes"/> (C7 / OQ-B).
/// </para>
///
/// <para>
/// <b>Tenant scope (ADR 0100 C3 / D1).</b> <see cref="TenantId"/> is the FIRST
/// positional parameter and is threaded into the payment, never derived from
/// the source. The party id is supplied already-resolved by the caller; the
/// importer never resolves a party itself.
/// </para>
/// </summary>
public interface IErpnextPaymentImporter
{
    /// <summary>
    /// Idempotent upsert of one ERPNext Payment Entry into a canonical
    /// <see cref="Payment"/> for (<paramref name="tenantId"/>, <paramref name="chartId"/>).
    /// </summary>
    /// <param name="tenantId">The single target tenant (ADR 0100 C3 — threaded, never derived).</param>
    /// <param name="source">The already-parsed ERPNext Payment Entry (ADR 0100 C6).</param>
    /// <param name="chartId">The destination chart-of-accounts.</param>
    /// <param name="partyPartyId">The pre-resolved customer/vendor party id (opaque to this importer).</param>
    /// <param name="authority">The import-run boundary's authenticated principal, tenant, and instant.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ImportOutcome<Payment>> UpsertPaymentAsync(
        TenantId tenantId,
        ErpnextPaymentSource source,
        ChartOfAccountsId chartId,
        PartyId partyPartyId,
        Harborline.Api.Foundation.Authorization.AuthorizationWriteContext authority,
        CancellationToken cancellationToken = default);
}
