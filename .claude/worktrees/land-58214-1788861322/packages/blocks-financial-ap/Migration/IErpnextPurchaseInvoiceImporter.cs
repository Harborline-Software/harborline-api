using Harborline.Api.Blocks.FinancialAp.Models;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Import.Outcomes;

namespace Harborline.Api.Blocks.FinancialAp.Migration;

/// <summary>
/// Upsert ERPNext <c>Purchase Invoice</c> records into the canonical AP
/// substrate. Pass-2 importer (the AP side); supplier resolution lives
/// upstream in <c>blocks-people-foundation.IErpnextPartyImporter</c>'s
/// pass-1 — by the time this importer runs, the caller already holds
/// the canonical <see cref="PartyId"/> for the source's
/// <c>supplier</c>.
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
/// converged signature shape of <c>IErpnextJournalEntryImporter</c> and
/// <c>IErpnextSalesInvoiceImporter</c>.
/// </para>
///
/// <para>
/// <b>Idempotency (ADR 0100 C7 / OQ-B).</b> the importer marks every imported
/// bill with <c>ExternalRef = "erpnext:pinv:{Name}"</c> (the FK) and stores the
/// source <c>Modified</c> stamp in the dedicated, indexable
/// <see cref="Bill.ExternalRefVersion"/> companion field — NO LONGER smuggled
/// into <see cref="Bill.Notes"/> (which is reserved for operator-facing free
/// text). Re-running with the same <c>Modified</c> returns the
/// <see cref="ImportOutcome{T}.Skipped"/> arm; a newer <c>Modified</c> returns
/// <see cref="ImportOutcome{T}.Updated"/> after rewriting the canonical row.
/// </para>
///
/// <para>
/// <b>No GL posting in v1.</b> Imported bills arrive at the AP substrate
/// already-balanced from ERPNext's perspective; we do NOT re-post a
/// journal entry. If the host wants the canonical GL to mirror ERPNext's,
/// that's a separate ledger-side importer flow.
/// </para>
/// </summary>
public interface IErpnextPurchaseInvoiceImporter
{
    /// <summary>
    /// Upsert a Purchase Invoice. Caller supplies cross-cluster fields
    /// (chart, supplier party, AP account, default expense account) that
    /// ERPNext's payload can't carry directly.
    /// </summary>
    /// <param name="tenantId">Multi-tenant scope (FIRST positional; ADR 0100 C3).</param>
    /// <param name="source">The ERPNext payload.</param>
    /// <param name="chartId">Chart-of-accounts this bill posts against.</param>
    /// <param name="vendorPartyId">Canonical Party id for the ERPNext supplier (resolved upstream).</param>
    /// <param name="apAccountId">AP control account.</param>
    /// <param name="defaultExpenseAccountId">Expense account used when a source line's <c>ExpenseAccount</c> is null.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<ImportOutcome<Bill>> UpsertPurchaseInvoiceAsync(
        TenantId tenantId,
        ErpnextPurchaseInvoiceSource source,
        ChartOfAccountsId chartId,
        PartyId vendorPartyId,
        GLAccountId apAccountId,
        GLAccountId defaultExpenseAccountId,
        CancellationToken cancellationToken = default);
}
