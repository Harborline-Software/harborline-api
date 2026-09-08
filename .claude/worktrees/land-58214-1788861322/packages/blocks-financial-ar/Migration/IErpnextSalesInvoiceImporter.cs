using Harborline.Api.Blocks.FinancialAr.Models;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Import.Outcomes;

namespace Harborline.Api.Blocks.FinancialAr.Migration;

/// <summary>
/// Upsert ERPNext <c>Sales Invoice</c> records into the canonical AR
/// substrate. Pass-2 importer (the AR side); customer resolution lives
/// upstream in <c>blocks-people-foundation.IErpnextPartyImporter</c>'s
/// pass-1 — by the time this importer runs, the caller already holds
/// the canonical <see cref="PartyId"/> for the source's
/// <c>customer</c>.
///
/// <para>
/// <b>Contract surface (ADR 0100).</b> Returns the canonical
/// <c>foundation-import</c> <see cref="ImportOutcome{T}"/> discriminated union
/// (Inserted | Updated | Skipped | Rejected) — NOT a local enum-based result —
/// so an orchestration pass can route every result into
/// <c>ImportCensus</c> via an exhaustive <c>switch</c> (ADR 0100 C2/OQ-A;
/// converged from the prior per-cluster <c>ImportOutcomeKind</c> copy). A record
/// that cannot be imported (missing required field) is the
/// <see cref="ImportOutcome{T}.Rejected"/> arm carrying a structured
/// <see cref="ImportFailure"/> — never a thrown exception across the contract
/// boundary and never a silent drop (ADR 0100 C2/C5).
/// </para>
///
/// <para>
/// <b>TenantId is the FIRST positional parameter</b> (ADR 0100 C3/D1) — threaded
/// from the orchestration layer, never derived from source data — matching the
/// converged signature shape of <c>IErpnextJournalEntryImporter</c>.
/// </para>
///
/// <para>
/// <b>Idempotency:</b> the importer marks every imported invoice with
/// <c>ExternalRef = "erpnext:sinv:{Name}"</c> (the FK) and stores
/// <c>erpnextModified:{Modified}</c> in <see cref="Invoice.Notes"/>.
/// Re-running with the same <c>Modified</c> returns the
/// <see cref="ImportOutcome{T}.Skipped"/> arm; a newer <c>Modified</c> returns
/// <see cref="ImportOutcome{T}.Updated"/> after rewriting the canonical row.
/// </para>
///
/// <para>
/// <b>No GL posting in v1.</b> Imported invoices arrive at the AR
/// substrate already-balanced from ERPNext's perspective; we do NOT
/// re-post a journal entry. If the host wants the canonical GL to
/// mirror ERPNext's GL, that's a separate ledger-side migration
/// importer flow.
/// </para>
/// </summary>
public interface IErpnextSalesInvoiceImporter
{
    /// <summary>
    /// Upsert a Sales Invoice. Caller supplies cross-cluster fields
    /// (chart, customer party, AR account, default income account)
    /// that ERPNext's payload can't carry directly.
    /// </summary>
    /// <param name="tenantId">Multi-tenant scope (FIRST positional; ADR 0100 C3).</param>
    /// <param name="source">The ERPNext payload.</param>
    /// <param name="chartId">Chart-of-accounts this invoice posts against.</param>
    /// <param name="customerPartyId">Canonical Party id for the ERPNext customer (resolved upstream).</param>
    /// <param name="arAccountId">AR control account.</param>
    /// <param name="defaultIncomeAccountId">Income account used when a source line's <c>IncomeAccount</c> is null.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<ImportOutcome<Invoice>> UpsertSalesInvoiceAsync(
        TenantId tenantId,
        ErpnextSalesInvoiceSource source,
        ChartOfAccountsId chartId,
        PartyId customerPartyId,
        GLAccountId arAccountId,
        GLAccountId defaultIncomeAccountId,
        CancellationToken cancellationToken = default);
}
