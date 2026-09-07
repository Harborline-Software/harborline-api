using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Harborline.Api.Blocks.FinancialAp.Migration;
using Harborline.Api.Blocks.FinancialAr.Migration;
using Harborline.Api.Blocks.FinancialLedger.Migration;
using Harborline.Api.Blocks.FinancialPayments.Migration;
using Harborline.Api.Blocks.FinancialPeriods.Migration;
using Harborline.Api.Blocks.FinancialTax.Migration;
using Harborline.Api.Blocks.People.Foundation.Migration;

namespace Harborline.Api.Blocks.Migration.Erpnext.Extraction;

/// <summary>
/// The A0 ERPNext source extractor — the C6 access-mode seam expressed in TYPED
/// terms (ADR 0100 C6). It sits one layer ABOVE the <c>foundation-import</c>
/// <see cref="Harborline.Api.Foundation.Import.Extraction.ISourceReader"/> generic
/// row-seam: where <c>ISourceReader</c> streams access-mode-agnostic
/// <see cref="Harborline.Api.Foundation.Import.Extraction.SourceRow"/>s (column maps),
/// this interface streams the FROZEN, typed <c>Erpnext*Source</c> DTOs the
/// upsert passes (A1–A6) consume — performing the row→DTO mapping, the
/// parent/child JOIN reconstruction, and the scalar coercion exactly once.
/// </summary>
/// <remarks>
/// <para>
/// <b>STRICTLY READ-ONLY (ADR 0100 C4 clean-room; C-CLEANROOM (a)).</b> Exposes
/// ONLY read operations — no write / update / delete / upsert / save against the
/// source. The importer never writes back to ERPNext; an arch-test asserts no
/// member of this interface (or any <c>*/Extraction/</c> type) performs a source
/// write.
/// </para>
/// <para>
/// <b>Access-mode-agnostic above the seam (C-MODE).</b> v1 ships exactly one
/// implementation — <see cref="MariaDbDumpExtractor"/>, composing the
/// dump-only <c>ISourceReader</c>. A future REST/CSV adapter implements THIS
/// interface with zero changes to A1–A6. The mode is visible ONLY via
/// <see cref="ReadInventoryAsync"/>'s <see cref="ErpnextSourceInventory.SourceMode"/>
/// provenance field (the C6 forward-hook).
/// </para>
/// <para>
/// <b>Streaming (<see cref="IAsyncEnumerable{T}"/>), not materialized lists.</b>
/// CIC's portfolio is ~10K records across DocTypes; streaming keeps the C5
/// &lt;5-min budget honest and avoids materializing every line at once. The
/// orchestrator threads each record into the matching upserter, which already
/// returns the C2 <c>ImportOutcome&lt;T&gt;</c> discriminated union.
/// </para>
/// <para>
/// <b>Faithful mirror, no policy.</b> The extractor reads ALL rows and surfaces
/// every field un-coerced where the DTO is a raw string (e.g. <c>AccountType</c>,
/// <c>VoucherType</c>, <c>DocStatus</c>): it does NOT pre-filter on
/// <c>docstatus</c>, does NOT default a NULL enum to a guess, and does NOT
/// resolve foreign keys — those are pass-level C5 concerns owned by the
/// upserters. The extractor's ONLY hard guard is the USD-only single-currency
/// invariant (ADR 0100 OQ-2): a non-USD transactional row fails loud
/// (out-of-v1-scope), it is never coerced to USD.
/// </para>
/// </remarks>
public interface IErpnextSourceExtractor
{
    // ---- Pass-1 chart ----

    /// <summary>Streams <c>tabAccount</c> rows as <see cref="ErpnextAccountSource"/> (A1 chart).</summary>
    IAsyncEnumerable<ErpnextAccountSource> ReadAccountsAsync(CancellationToken ct = default);

    /// <summary>Streams <c>tabCost Center</c> rows as <see cref="ErpnextCostCenterSource"/> (A1 cost-centers).</summary>
    IAsyncEnumerable<ErpnextCostCenterSource> ReadCostCentersAsync(CancellationToken ct = default);

    // ---- Pass-2 reference data ----

    /// <summary>Streams <c>tabFiscal Year</c> rows as <see cref="ErpnextFiscalYearSource"/> (A2.2 periods).</summary>
    IAsyncEnumerable<ErpnextFiscalYearSource> ReadFiscalYearsAsync(CancellationToken ct = default);

    /// <summary>Streams <c>tabCustomer</c> rows as <see cref="ErpnextPartyCustomerSource"/> (A2.1 parties).</summary>
    IAsyncEnumerable<ErpnextPartyCustomerSource> ReadCustomersAsync(CancellationToken ct = default);

    /// <summary>Streams <c>tabSupplier</c> rows as <see cref="ErpnextPartySupplierSource"/> (A2.1 parties).</summary>
    IAsyncEnumerable<ErpnextPartySupplierSource> ReadSuppliersAsync(CancellationToken ct = default);

    /// <summary>
    /// Streams <c>tabContact</c> rows as <see cref="ErpnextContactSource"/> with their
    /// <c>tabDynamic Link</c> party links reconstructed in-process (A2.1 parties).
    /// </summary>
    IAsyncEnumerable<ErpnextContactSource> ReadContactsAsync(CancellationToken ct = default);

    /// <summary>
    /// Streams <c>tabAddress</c> rows as <see cref="ErpnextAddressSource"/> with their
    /// <c>tabDynamic Link</c> party links reconstructed in-process (A2.1 parties).
    /// </summary>
    IAsyncEnumerable<ErpnextAddressSource> ReadAddressesAsync(CancellationToken ct = default);

    /// <summary>
    /// Streams sales + purchase tax templates as <see cref="ErpnextTaxTemplateSource"/>
    /// with their child rate rows reconstructed in-process (A2.3 tax).
    /// </summary>
    IAsyncEnumerable<ErpnextTaxTemplateSource> ReadTaxTemplatesAsync(CancellationToken ct = default);

    // ---- Pass-3/4 transactional (header + child JOIN reconstruction) ----

    /// <summary>
    /// Streams <c>tabJournal Entry</c> rows joined to their <c>tabJournal Entry Account</c>
    /// lines as <see cref="ErpnextJournalEntrySource"/> (A3 opening + A4.4 manual JEs).
    /// </summary>
    IAsyncEnumerable<ErpnextJournalEntrySource> ReadJournalEntriesAsync(CancellationToken ct = default);

    /// <summary>
    /// Streams <c>tabSales Invoice</c> rows joined to their <c>tabSales Invoice Item</c>
    /// lines as <see cref="ErpnextSalesInvoiceSource"/> (A4.1). USD-only guarded.
    /// </summary>
    IAsyncEnumerable<ErpnextSalesInvoiceSource> ReadSalesInvoicesAsync(CancellationToken ct = default);

    /// <summary>
    /// Streams <c>tabPurchase Invoice</c> rows joined to their <c>tabPurchase Invoice Item</c>
    /// lines as <see cref="ErpnextPurchaseInvoiceSource"/> (A4.2). USD-only guarded.
    /// </summary>
    IAsyncEnumerable<ErpnextPurchaseInvoiceSource> ReadPurchaseInvoicesAsync(CancellationToken ct = default);

    /// <summary>
    /// Streams <c>tabPayment Entry</c> rows as <see cref="ErpnextPaymentSource"/> (A4.3).
    /// USD-only guarded.
    /// </summary>
    IAsyncEnumerable<ErpnextPaymentSource> ReadPaymentsAsync(CancellationToken ct = default);

    // ---- Census ----

    /// <summary>
    /// Enumerates every <c>tab*</c> DocType present in the source and partitions it
    /// into mapped / known-irrelevant / unmapped-unknown buckets (ADR 0100 C5 /
    /// the report <c>_unmapped/</c> section). Unmapped-unknown DocTypes are COUNTED
    /// and LISTED, never silently dropped — this is how a custom
    /// <c>tabProperty</c>/<c>tabLease</c>/<c>tabUnit</c> surfaces for CIC review.
    /// Carries a <c>SourceMode</c> provenance descriptor (C6 forward-hook).
    /// </summary>
    Task<ErpnextSourceInventory> ReadInventoryAsync(CancellationToken ct = default);
}
