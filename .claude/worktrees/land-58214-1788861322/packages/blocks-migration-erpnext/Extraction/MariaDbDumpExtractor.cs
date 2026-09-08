// ---------------------------------------------------------------------------
// CLEAN-ROOM ATTRIBUTION (ADR 0100 C4 / C-CLEANROOM (b); spec §9.5)
//
// This extractor reads the DATA FORMAT of an ERPNext / Frappe MariaDB dump only —
// the public data-interchange shape (table rows and column names), via the
// foundation-import MariaDbDumpSourceReader it composes. It derives NO code from
// ERPNext or Frappe: no controllers, validators, workflow logic, or
// DocType-definition JSON. ERPNext and the Frappe Framework are projects of
// Frappe Technologies Pvt. Ltd., licensed under the GNU GPLv3. This file is
// FORMAT-REFERENCE-ONLY; NO GPLv3 code is derived or copied. Harborline Software
// code is MIT-licensed (see Directory.Build.props PackageLicenseExpression).
// ---------------------------------------------------------------------------

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Harborline.Api.Blocks.FinancialAp.Migration;
using Harborline.Api.Blocks.FinancialAr.Migration;
using Harborline.Api.Blocks.FinancialLedger.Migration;
using Harborline.Api.Blocks.FinancialPayments.Migration;
using Harborline.Api.Blocks.FinancialPeriods.Migration;
using Harborline.Api.Blocks.FinancialTax.Migration;
using Harborline.Api.Blocks.People.Foundation.Migration;
using Harborline.Api.Foundation.Import.Extraction;

namespace Harborline.Api.Blocks.Migration.Erpnext.Extraction;

/// <summary>
/// v1's SOLE ERPNext source extractor (ADR 0100 C6) — projects the access-mode-
/// agnostic <see cref="SourceRow"/>s produced by a <see cref="ISourceReader"/>
/// into the FROZEN, typed <c>Erpnext*Source</c> DTOs the upsert passes consume.
/// </summary>
/// <remarks>
/// <para>
/// <b>Composes, does not reimplement.</b> The dump parsing — the mysqldump
/// dialect handling, the <c>INSERT</c>-value tokenizer, MySQL string escaping,
/// the <c>tab&lt;DocType&gt;</c> discovery — all live in the foundation-import
/// <see cref="MariaDbDumpSourceReader"/> (#183). This extractor adds ONLY the
/// typed-DTO layer on top: row→DTO field mapping, scalar coercion (via
/// <see cref="ErpnextFieldReader"/>), the in-process parent/child JOIN
/// reconstruction, the USD-only guard, and the C5 DocType census.
/// </para>
/// <para>
/// <b>Streaming-vs-restore (the directive's adjudication seam — see PR
/// description).</b> The #194 design recommended restoring the dump into a
/// throwaway DB and issuing real SQL JOINs. v1 instead composes the SHIPPED #183
/// string-parse reader and reconstructs parent/child documents in MANAGED MEMORY
/// (group child rows by the <c>parent</c> column). This choice KEEPS the
/// C-CLEANROOM "no DB connection / no network" arch-test true — a restore-to-DB
/// path would have introduced a live ADO.NET database connection and broken it. Because
/// the access strategy lives BELOW the <see cref="ISourceReader"/> seam, swapping
/// to a future <c>RestoreToDbSourceReader : ISourceReader</c> is an additive,
/// A1–A6-invisible refactor — exactly what the seam exists for.
/// </para>
/// <para>
/// <b>Read-only / offline / clean-room.</b> Inherits the composed reader's posture:
/// no write/update/delete against source, no network, no SQL execution. Logs (when
/// the orchestrator wires them) emit only DocType / opaque externalRef / counts.
/// </para>
/// </remarks>
public sealed class MariaDbDumpExtractor : IErpnextSourceExtractor
{
    private readonly ISourceReader _reader;

    // ---- ERPNext DocType names (the keys ISourceReader streams by). ----
    private const string AccountDocType = "Account";
    private const string CostCenterDocType = "Cost Center";
    private const string FiscalYearDocType = "Fiscal Year";
    private const string FiscalYearCompanyDocType = "Fiscal Year Company";
    private const string CustomerDocType = "Customer";
    private const string SupplierDocType = "Supplier";
    private const string ContactDocType = "Contact";
    private const string AddressDocType = "Address";
    private const string DynamicLinkDocType = "Dynamic Link";
    private const string SalesTaxTemplateDocType = "Sales Taxes and Charges Template";
    private const string PurchaseTaxTemplateDocType = "Purchase Taxes and Charges Template";
    private const string SalesTaxChargeDocType = "Sales Taxes and Charges";
    private const string PurchaseTaxChargeDocType = "Purchase Taxes and Charges";
    private const string JournalEntryDocType = "Journal Entry";
    private const string JournalEntryAccountDocType = "Journal Entry Account";
    private const string SalesInvoiceDocType = "Sales Invoice";
    private const string SalesInvoiceItemDocType = "Sales Invoice Item";
    private const string PurchaseInvoiceDocType = "Purchase Invoice";
    private const string PurchaseInvoiceItemDocType = "Purchase Invoice Item";
    private const string PaymentEntryDocType = "Payment Entry";

    // The Frappe child-table correlation column (every child row points at its parent's `name`).
    private const string ParentColumn = "parent";

    /// <summary>
    /// Constructs the extractor over a composed <see cref="ISourceReader"/> — v1's
    /// reader is the foundation-import <see cref="MariaDbDumpSourceReader"/>, loaded
    /// per-run by the CLI (A7) from a CIC-supplied dump-file path.
    /// </summary>
    /// <param name="reader">The composed source reader (read-only seam).</param>
    public MariaDbDumpExtractor(ISourceReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        _reader = reader;
    }

    // ===================================================================
    //  Pass-1 chart
    // ===================================================================

    /// <inheritdoc />
    public async IAsyncEnumerable<ErpnextAccountSource> ReadAccountsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var row in _reader.ReadDocTypeAsync(AccountDocType, ct).ConfigureAwait(false))
        {
            var externalRef = ErpnextFieldReader.ExternalRef(row);
            yield return new ErpnextAccountSource(
                Name: externalRef,
                Modified: ErpnextFieldReader.StringOrFallback(row, "modified", string.Empty),
                AccountName: ErpnextFieldReader.StringOrFallback(row, "account_name", externalRef),
                AccountNumber: ErpnextFieldReader.OptionalString(row, "account_number"),
                ParentAccountName: ErpnextFieldReader.OptionalString(row, "parent_account"),
                AccountType: ErpnextFieldReader.OptionalString(row, "account_type"),
                IsGroup: ErpnextFieldReader.Bool(row, externalRef, "is_group"),
                Disabled: ErpnextFieldReader.Bool(row, externalRef, "disabled"));
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ErpnextCostCenterSource> ReadCostCentersAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var row in _reader.ReadDocTypeAsync(CostCenterDocType, ct).ConfigureAwait(false))
        {
            var externalRef = ErpnextFieldReader.ExternalRef(row);
            yield return new ErpnextCostCenterSource(
                Name: externalRef,
                Modified: ErpnextFieldReader.StringOrFallback(row, "modified", string.Empty),
                CostCenterName: ErpnextFieldReader.StringOrFallback(row, "cost_center_name", externalRef),
                ParentCostCenterName: ErpnextFieldReader.OptionalString(row, "parent_cost_center"),
                IsGroup: ErpnextFieldReader.Bool(row, externalRef, "is_group"),
                Disabled: ErpnextFieldReader.Bool(row, externalRef, "disabled"));
        }
    }

    // ===================================================================
    //  Pass-2 reference data
    // ===================================================================

    /// <inheritdoc />
    public async IAsyncEnumerable<ErpnextFiscalYearSource> ReadFiscalYearsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Optional child: fiscal-year company short-name (first linked company, if any).
        var companyByParent = await GroupChildrenByParentAsync(FiscalYearCompanyDocType, ct).ConfigureAwait(false);

        await foreach (var row in _reader.ReadDocTypeAsync(FiscalYearDocType, ct).ConfigureAwait(false))
        {
            var externalRef = ErpnextFieldReader.ExternalRef(row);
            string? companyShortName = null;
            if (companyByParent.TryGetValue(externalRef, out var companies) && companies.Count > 0)
            {
                companyShortName = ErpnextFieldReader.OptionalString(companies[0], "company");
            }

            yield return new ErpnextFiscalYearSource(
                Name: externalRef,
                Modified: ErpnextFieldReader.StringOrFallback(row, "modified", string.Empty),
                YearStartDate: ErpnextFieldReader.RequiredDate(row, externalRef, "year_start_date"),
                YearEndDate: ErpnextFieldReader.RequiredDate(row, externalRef, "year_end_date"),
                CompanyShortName: companyShortName,
                IsShortYear: ErpnextFieldReader.Bool(row, externalRef, "is_short_year"));
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ErpnextPartyCustomerSource> ReadCustomersAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var row in _reader.ReadDocTypeAsync(CustomerDocType, ct).ConfigureAwait(false))
        {
            var externalRef = ErpnextFieldReader.ExternalRef(row);
            yield return new ErpnextPartyCustomerSource(
                Name: externalRef,
                Modified: ErpnextFieldReader.StringOrFallback(row, "modified", string.Empty),
                CustomerName: ErpnextFieldReader.StringOrFallback(row, "customer_name", externalRef),
                CustomerType: ErpnextFieldReader.OptionalString(row, "customer_type"),
                EmailId: ErpnextFieldReader.OptionalString(row, "email_id"),
                MobileNo: ErpnextFieldReader.OptionalString(row, "mobile_no"),
                TaxId: ErpnextFieldReader.OptionalString(row, "tax_id"),
                Disabled: ErpnextFieldReader.Bool(row, externalRef, "disabled"));
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ErpnextPartySupplierSource> ReadSuppliersAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var row in _reader.ReadDocTypeAsync(SupplierDocType, ct).ConfigureAwait(false))
        {
            var externalRef = ErpnextFieldReader.ExternalRef(row);
            yield return new ErpnextPartySupplierSource(
                Name: externalRef,
                Modified: ErpnextFieldReader.StringOrFallback(row, "modified", string.Empty),
                SupplierName: ErpnextFieldReader.StringOrFallback(row, "supplier_name", externalRef),
                SupplierType: ErpnextFieldReader.OptionalString(row, "supplier_type"),
                EmailId: ErpnextFieldReader.OptionalString(row, "email_id"),
                MobileNo: ErpnextFieldReader.OptionalString(row, "mobile_no"),
                TaxId: ErpnextFieldReader.OptionalString(row, "tax_id"),
                Disabled: ErpnextFieldReader.Bool(row, externalRef, "disabled"));
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ErpnextContactSource> ReadContactsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var linksByParent = await GroupDynamicLinksByParentAsync(ct).ConfigureAwait(false);

        await foreach (var row in _reader.ReadDocTypeAsync(ContactDocType, ct).ConfigureAwait(false))
        {
            var externalRef = ErpnextFieldReader.ExternalRef(row);
            var links = linksByParent.TryGetValue(externalRef, out var l) ? l : EmptyLinks;
            yield return new ErpnextContactSource(
                Name: externalRef,
                EmailId: ErpnextFieldReader.OptionalString(row, "email_id"),
                MobileNo: ErpnextFieldReader.OptionalString(row, "mobile_no"),
                Links: links);
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ErpnextAddressSource> ReadAddressesAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var linksByParent = await GroupDynamicLinksByParentAsync(ct).ConfigureAwait(false);

        await foreach (var row in _reader.ReadDocTypeAsync(AddressDocType, ct).ConfigureAwait(false))
        {
            var externalRef = ErpnextFieldReader.ExternalRef(row);
            var links = linksByParent.TryGetValue(externalRef, out var l) ? l : EmptyLinks;
            yield return new ErpnextAddressSource(
                Name: externalRef,
                AddressLine1: ErpnextFieldReader.RequiredString(row, externalRef, "address_line1"),
                City: ErpnextFieldReader.RequiredString(row, externalRef, "city"),
                State: ErpnextFieldReader.RequiredString(row, externalRef, "state"),
                Pincode: ErpnextFieldReader.RequiredString(row, externalRef, "pincode"),
                Country: ErpnextFieldReader.RequiredString(row, externalRef, "country"),
                AddressLine2: ErpnextFieldReader.OptionalString(row, "address_line2"),
                IsPrimaryAddress: ErpnextFieldReader.Bool(row, externalRef, "is_primary_address"),
                Links: links);
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ErpnextTaxTemplateSource> ReadTaxTemplatesAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Sales + purchase templates share one DTO; each has its own child rate table.
        var salesRates = await GroupChildrenByParentAsync(SalesTaxChargeDocType, ct).ConfigureAwait(false);
        await foreach (var template in ReadTaxTemplateFamilyAsync(SalesTaxTemplateDocType, salesRates, ct).ConfigureAwait(false))
        {
            yield return template;
        }

        var purchaseRates = await GroupChildrenByParentAsync(PurchaseTaxChargeDocType, ct).ConfigureAwait(false);
        await foreach (var template in ReadTaxTemplateFamilyAsync(PurchaseTaxTemplateDocType, purchaseRates, ct).ConfigureAwait(false))
        {
            yield return template;
        }
    }

    private async IAsyncEnumerable<ErpnextTaxTemplateSource> ReadTaxTemplateFamilyAsync(
        string templateDocType,
        IReadOnlyDictionary<string, List<SourceRow>> ratesByParent,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var row in _reader.ReadDocTypeAsync(templateDocType, ct).ConfigureAwait(false))
        {
            var externalRef = ErpnextFieldReader.ExternalRef(row);
            var rateRows = new List<ErpnextTaxTemplateRateRow>();
            if (ratesByParent.TryGetValue(externalRef, out var children))
            {
                foreach (var child in children)
                {
                    rateRows.Add(new ErpnextTaxTemplateRateRow(
                        AccountHead: ErpnextFieldReader.RequiredString(child, externalRef, "account_head"),
                        Rate: ErpnextFieldReader.DecimalOrDefault(child, externalRef, "rate", 0m),
                        IncludedInPrintRate: ErpnextFieldReader.Bool(child, externalRef, "included_in_print_rate")));
                }
            }

            yield return new ErpnextTaxTemplateSource(
                Name: externalRef,
                Modified: ErpnextFieldReader.StringOrFallback(row, "modified", string.Empty),
                TaxName: ErpnextFieldReader.StringOrFallback(row, "title", externalRef),
                TaxCategory: ErpnextFieldReader.OptionalString(row, "tax_category"),
                Rates: rateRows,
                Disabled: ErpnextFieldReader.Bool(row, externalRef, "disabled"));
        }
    }

    // ===================================================================
    //  Pass-3/4 transactional (header + child JOIN reconstruction)
    // ===================================================================

    /// <inheritdoc />
    public async IAsyncEnumerable<ErpnextJournalEntrySource> ReadJournalEntriesAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var linesByParent = await GroupChildrenByParentAsync(JournalEntryAccountDocType, ct).ConfigureAwait(false);

        await foreach (var row in _reader.ReadDocTypeAsync(JournalEntryDocType, ct).ConfigureAwait(false))
        {
            var externalRef = ErpnextFieldReader.ExternalRef(row);
            var lines = new List<ErpnextJournalEntryLineSource>();
            if (linesByParent.TryGetValue(externalRef, out var children))
            {
                foreach (var child in children)
                {
                    lines.Add(new ErpnextJournalEntryLineSource(
                        AccountName: ErpnextFieldReader.RequiredString(child, externalRef, "account"),
                        DebitInAccountCurrency: ErpnextFieldReader.DecimalOrDefault(child, externalRef, "debit_in_account_currency", 0m),
                        CreditInAccountCurrency: ErpnextFieldReader.DecimalOrDefault(child, externalRef, "credit_in_account_currency", 0m),
                        CostCenter: ErpnextFieldReader.OptionalString(child, "cost_center"),
                        UserRemark: ErpnextFieldReader.OptionalString(child, "user_remark")));
                }
            }

            yield return new ErpnextJournalEntrySource(
                Name: externalRef,
                Modified: ErpnextFieldReader.StringOrFallback(row, "modified", string.Empty),
                PostingDate: ErpnextFieldReader.RequiredDate(row, externalRef, "posting_date"),
                Memo: ErpnextFieldReader.StringOrFallback(row, "user_remark", string.Empty),
                VoucherType: ErpnextFieldReader.StringOrFallback(row, "voucher_type", string.Empty),
                IsOpening: ReadIsOpening(row, externalRef),
                DocStatus: ErpnextFieldReader.IntOrDefault(row, externalRef, "docstatus", 0),
                Lines: lines);
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ErpnextSalesInvoiceSource> ReadSalesInvoicesAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var itemsByParent = await GroupChildrenByParentAsync(SalesInvoiceItemDocType, ct).ConfigureAwait(false);

        await foreach (var row in _reader.ReadDocTypeAsync(SalesInvoiceDocType, ct).ConfigureAwait(false))
        {
            var externalRef = ErpnextFieldReader.ExternalRef(row);
            // USD-only guard (ADR 0100 OQ-2) — non-USD fails loud, never coerced.
            var currency = ErpnextFieldReader.RequireUsdCurrency(row, externalRef);

            var items = new List<ErpnextSalesInvoiceItem>();
            if (itemsByParent.TryGetValue(externalRef, out var children))
            {
                foreach (var child in children)
                {
                    items.Add(new ErpnextSalesInvoiceItem(
                        ItemName: ErpnextFieldReader.StringOrFallback(child, "item_name", string.Empty),
                        Qty: ErpnextFieldReader.DecimalOrDefault(child, externalRef, "qty", 0m),
                        Rate: ErpnextFieldReader.DecimalOrDefault(child, externalRef, "rate", 0m),
                        Amount: ErpnextFieldReader.DecimalOrDefault(child, externalRef, "amount", 0m),
                        IncomeAccount: ErpnextFieldReader.OptionalString(child, "income_account"),
                        CostCenter: ErpnextFieldReader.OptionalString(child, "cost_center")));
                }
            }

            yield return new ErpnextSalesInvoiceSource(
                Name: externalRef,
                Modified: ErpnextFieldReader.StringOrFallback(row, "modified", string.Empty),
                Customer: ErpnextFieldReader.RequiredString(row, externalRef, "customer"),
                PostingDate: ErpnextFieldReader.RequiredDate(row, externalRef, "posting_date"),
                DueDate: ErpnextFieldReader.RequiredDate(row, externalRef, "due_date"),
                Currency: currency,
                Items: items,
                Status: ErpnextFieldReader.StringOrFallback(row, "status", string.Empty),
                GrandTotal: ErpnextFieldReader.DecimalOrDefault(row, externalRef, "grand_total", 0m),
                OutstandingAmount: ErpnextFieldReader.DecimalOrDefault(row, externalRef, "outstanding_amount", 0m));
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ErpnextPurchaseInvoiceSource> ReadPurchaseInvoicesAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var itemsByParent = await GroupChildrenByParentAsync(PurchaseInvoiceItemDocType, ct).ConfigureAwait(false);

        await foreach (var row in _reader.ReadDocTypeAsync(PurchaseInvoiceDocType, ct).ConfigureAwait(false))
        {
            var externalRef = ErpnextFieldReader.ExternalRef(row);
            var currency = ErpnextFieldReader.RequireUsdCurrency(row, externalRef);

            var items = new List<ErpnextPurchaseInvoiceItem>();
            if (itemsByParent.TryGetValue(externalRef, out var children))
            {
                foreach (var child in children)
                {
                    items.Add(new ErpnextPurchaseInvoiceItem(
                        ItemName: ErpnextFieldReader.StringOrFallback(child, "item_name", string.Empty),
                        Qty: ErpnextFieldReader.DecimalOrDefault(child, externalRef, "qty", 0m),
                        Rate: ErpnextFieldReader.DecimalOrDefault(child, externalRef, "rate", 0m),
                        Amount: ErpnextFieldReader.DecimalOrDefault(child, externalRef, "amount", 0m),
                        ExpenseAccount: ErpnextFieldReader.OptionalString(child, "expense_account"),
                        CostCenter: ErpnextFieldReader.OptionalString(child, "cost_center")));
                }
            }

            yield return new ErpnextPurchaseInvoiceSource(
                Name: externalRef,
                Modified: ErpnextFieldReader.StringOrFallback(row, "modified", string.Empty),
                Supplier: ErpnextFieldReader.RequiredString(row, externalRef, "supplier"),
                BillNo: ErpnextFieldReader.OptionalString(row, "bill_no"),
                PostingDate: ErpnextFieldReader.RequiredDate(row, externalRef, "posting_date"),
                DueDate: ErpnextFieldReader.RequiredDate(row, externalRef, "due_date"),
                BillDate: ErpnextFieldReader.OptionalDate(row, externalRef, "bill_date"),
                Currency: currency,
                Items: items,
                Status: ErpnextFieldReader.StringOrFallback(row, "status", string.Empty),
                GrandTotal: ErpnextFieldReader.DecimalOrDefault(row, externalRef, "grand_total", 0m),
                OutstandingAmount: ErpnextFieldReader.DecimalOrDefault(row, externalRef, "outstanding_amount", 0m));
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ErpnextPaymentSource> ReadPaymentsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var row in _reader.ReadDocTypeAsync(PaymentEntryDocType, ct).ConfigureAwait(false))
        {
            var externalRef = ErpnextFieldReader.ExternalRef(row);
            var currency = ErpnextFieldReader.RequireUsdCurrency(row, externalRef);

            yield return new ErpnextPaymentSource(
                Name: externalRef,
                Modified: ErpnextFieldReader.StringOrFallback(row, "modified", string.Empty),
                PaymentType: ErpnextFieldReader.RequiredString(row, externalRef, "payment_type"),
                ModeOfPayment: ErpnextFieldReader.OptionalString(row, "mode_of_payment"),
                Party: ErpnextFieldReader.StringOrFallback(row, "party", string.Empty),
                PostingDate: ErpnextFieldReader.RequiredDate(row, externalRef, "posting_date"),
                PaidAmount: ErpnextFieldReader.DecimalOrDefault(row, externalRef, "paid_amount", 0m),
                UnallocatedAmount: ErpnextFieldReader.DecimalOrDefault(row, externalRef, "unallocated_amount", 0m),
                Currency: currency,
                ReferenceNo: ErpnextFieldReader.OptionalString(row, "reference_no"),
                PaidFromAccount: ErpnextFieldReader.OptionalString(row, "paid_from"),
                PaidToAccount: ErpnextFieldReader.OptionalString(row, "paid_to"),
                DocStatus: ErpnextFieldReader.IntOrDefault(row, externalRef, "docstatus", 0));
        }
    }

    // ===================================================================
    //  Census (C5)
    // ===================================================================

    /// <inheritdoc />
    public async Task<ErpnextSourceInventory> ReadInventoryAsync(CancellationToken ct = default)
    {
        var entries = new List<ErpnextDocTypeCensusEntry>();
        foreach (var docType in _reader.AvailableDocTypes)
        {
            ct.ThrowIfCancellationRequested();
            var classification = ErpnextDocTypeMap.Classify(docType);
            var count = await _reader.CountDocTypeAsync(docType, ct).ConfigureAwait(false);
            entries.Add(new ErpnextDocTypeCensusEntry(docType, classification, count));
        }

        return new ErpnextSourceInventory(entries, _reader.Mode);
    }

    // ===================================================================
    //  In-process parent/child JOIN reconstruction (replaces a DB JOIN)
    // ===================================================================

    private static readonly IReadOnlyList<ErpnextDynamicLink> EmptyLinks = System.Array.Empty<ErpnextDynamicLink>();

    /// <summary>
    /// Reads ERPNext's <c>is_opening</c> on a Journal Entry — a varchar enum
    /// (<c>"Yes"</c>/<c>"No"</c>) in ERPNext, NOT a tinyint, so it is mapped here
    /// rather than via the boolean reader. Absent/null/anything-but-"Yes" -> false.
    /// </summary>
    private static bool ReadIsOpening(SourceRow row, string externalRef)
    {
        _ = externalRef;
        var raw = row.GetString("is_opening");
        return string.Equals(raw?.Trim(), "Yes", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Buffers all rows of a child DocType and groups them by their <c>parent</c>
    /// column — the in-process equivalent of a parent/child SQL JOIN. The child
    /// tables are bounded (line items / rate rows / links), so buffering them is
    /// the same memory shape the composed reader already holds; the parent headers
    /// still stream. An empty/absent child table yields an empty index.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, List<SourceRow>>> GroupChildrenByParentAsync(
        string childDocType, CancellationToken ct)
    {
        var byParent = new Dictionary<string, List<SourceRow>>(StringComparer.Ordinal);
        await foreach (var child in _reader.ReadDocTypeAsync(childDocType, ct).ConfigureAwait(false))
        {
            var parent = child.GetString(ParentColumn);
            if (string.IsNullOrEmpty(parent))
            {
                // A child row with no parent is an orphan — surface as a structured
                // failure rather than silently dropping it (C5 no-silent-drop). The
                // child's own `name` is the externalRef for the message.
                throw new ErpnextExtractionException(
                    childDocType,
                    child.GetString("name") ?? "(unknown)",
                    ErpnextExtractionReason.MissingRequiredField,
                    ParentColumn);
            }

            if (!byParent.TryGetValue(parent, out var list))
            {
                list = new List<SourceRow>();
                byParent[parent] = list;
            }

            list.Add(child);
        }

        return byParent;
    }

    /// <summary>
    /// Groups <c>tabDynamic Link</c> rows by their <c>parent</c> (the owning Contact /
    /// Address <c>name</c>), projecting each into an <see cref="ErpnextDynamicLink"/>.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, IReadOnlyList<ErpnextDynamicLink>>> GroupDynamicLinksByParentAsync(
        CancellationToken ct)
    {
        var raw = await GroupChildrenByParentAsync(DynamicLinkDocType, ct).ConfigureAwait(false);
        var result = new Dictionary<string, IReadOnlyList<ErpnextDynamicLink>>(StringComparer.Ordinal);
        foreach (var (parent, children) in raw)
        {
            var links = new List<ErpnextDynamicLink>(children.Count);
            foreach (var child in children)
            {
                var parentRef = child.GetString("name") ?? parent;
                links.Add(new ErpnextDynamicLink(
                    LinkDocType: ErpnextFieldReader.RequiredString(child, parentRef, "link_doctype"),
                    LinkName: ErpnextFieldReader.RequiredString(child, parentRef, "link_name")));
            }

            result[parent] = links;
        }

        return result;
    }
}
