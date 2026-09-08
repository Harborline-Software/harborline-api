using Harborline.Api.Blocks.FinancialAp.Models;
using Harborline.Api.Blocks.FinancialAp.Services;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Import.Outcomes;

namespace Harborline.Api.Blocks.FinancialAp.Migration;

/// <summary>
/// Default <see cref="IErpnextPurchaseInvoiceImporter"/>. Uses
/// <see cref="IBillRepository"/> directly. The bill carries the
/// vendor's own number from ERPNext's <c>bill_no</c> (falling back to
/// <c>name</c> when bill_no is blank) — AP doesn't mint canonical
/// numbers like AR does.
///
/// <para>
/// Returns the <c>foundation-import</c> <see cref="ImportOutcome{T}"/> DU
/// (ADR 0100 C2/OQ-A). A missing required field on the source becomes the
/// <see cref="ImportOutcome{T}.Rejected"/> arm carrying a structured,
/// allowlisted <see cref="ImportFailure"/> (ADR 0100 C9 — the reject carries
/// the opaque ERPNext <c>name</c> + DocType + reason code + offending field
/// NAME only; never a party name/address or a monetary value).
/// </para>
///
/// <para>
/// Idempotency uses the dedicated <see cref="Bill.ExternalRefVersion"/> field
/// (ADR 0100 C7) — the source <c>Modified</c> stamp is stored there, NOT in
/// <see cref="Bill.Notes"/>.
/// </para>
/// </summary>
public sealed class ErpnextPurchaseInvoiceImporter : IErpnextPurchaseInvoiceImporter
{
    private const string ExternalRefPrefix = "erpnext:pinv:";

    /// <summary>The ERPNext DocType this importer consumes — used for reject provenance (ADR 0100 C9).</summary>
    public const string DocType = "Purchase Invoice";

    private readonly IBillRepository _bills;
    private readonly TimeProvider _time;

    public ErpnextPurchaseInvoiceImporter(IBillRepository bills, TimeProvider time)
    {
        _bills = bills ?? throw new ArgumentNullException(nameof(bills));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <inheritdoc />
    public async Task<ImportOutcome<Bill>> UpsertPurchaseInvoiceAsync(
        TenantId tenantId,
        ErpnextPurchaseInvoiceSource source,
        ChartOfAccountsId chartId,
        PartyId vendorPartyId,
        GLAccountId apAccountId,
        GLAccountId defaultExpenseAccountId,
        CancellationToken cancellationToken = default)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));

        // Missing-required-field rejects. The reject carries only the opaque
        // ERPNext name + the offending field NAME — never the field's value
        // (ADR 0100 C9). When the name itself is empty we have no natural key
        // to cite, so fall back to a sentinel marker.
        if (string.IsNullOrWhiteSpace(source.Name))
            return Reject("(unknown)", "name", "ERPNext PurchaseInvoice.name is empty.");
        if (string.IsNullOrWhiteSpace(source.Supplier))
            return Reject(source.Name, "supplier", "ERPNext PurchaseInvoice.supplier is empty.");
        if (source.Items is null || source.Items.Count == 0)
            return Reject(source.Name, "items", "ERPNext PurchaseInvoice has no items.");

        var externalRef = ExternalRefPrefix + source.Name;

        var existing = await _bills.GetByExternalRefAsync(tenantId, chartId, externalRef, cancellationToken)
            .ConfigureAwait(false);

        // C7 idempotency: compare the source Modified stamp against the dedicated
        // ExternalRefVersion companion field (NOT a marker smuggled into Notes).
        if (existing is not null
            && string.Equals(existing.ExternalRefVersion, source.Modified, StringComparison.Ordinal))
        {
            return new ImportOutcome<Bill>.Skipped(
                existing,
                $"Already imported at modified={source.Modified}.");
        }

        var canonicalStatus = MapStatus(source.Status);
        var amountPaid = source.GrandTotal - source.OutstandingAmount;
        if (amountPaid < 0m) amountPaid = 0m;

        var billId = existing?.Id ?? BillId.NewId();
        var billNumber = string.IsNullOrWhiteSpace(source.BillNo) ? source.Name : source.BillNo!;
        var lines = new List<BillLine>(source.Items.Count);
        var lineNo = 1;
        foreach (var item in source.Items)
        {
            var debitAccount = !string.IsNullOrEmpty(item.ExpenseAccount)
                ? new GLAccountId(item.ExpenseAccount!)
                : defaultExpenseAccountId;
            var amount = item.Amount > 0m
                ? item.Amount
                : decimal.Round(item.Qty * item.Rate, 2, MidpointRounding.ToEven);

            lines.Add(new BillLine
            {
                Id = BillLineId.NewId(),
                BillId = billId,
                LineNumber = lineNo++,
                Description = item.ItemName,
                Quantity = item.Qty,
                UnitPrice = item.Rate,
                Amount = amount,
                DebitAccountId = debitAccount,
                PropertyId = item.CostCenter,
            });
        }

        var currency = string.IsNullOrWhiteSpace(source.Currency) ? "USD" : source.Currency!;

        var admittedAt = _time.GetUtcNow();
        var now = new Instant(admittedAt);
        var subtotal = lines.Sum(l => l.Amount);
        var taxTotal = source.GrandTotal - subtotal;
        if (taxTotal < 0m) taxTotal = 0m;
        var total = subtotal + taxTotal;
        var balance = total - amountPaid;
        if (balance < 0m) balance = 0m;

        var bill = new Bill
        {
            Id = billId,
            TenantId = tenantId,
            ChartId = chartId,
            BillNumber = billNumber,
            VendorId = vendorPartyId,
            BillDate = source.BillDate ?? source.PostingDate,
            DueDate = source.DueDate,
            ReceivedDate = source.PostingDate,
            Currency = currency,
            Lines = lines,
            Subtotal = subtotal,
            TaxTotal = taxTotal,
            Total = total,
            AmountPaid = amountPaid,
            Balance = balance,
            Status = canonicalStatus,
            ApAccountId = apAccountId,
            ExternalRef = externalRef,
            ExternalRefVersion = source.Modified,
            Notes = existing?.Notes,
            CreatedAtUtc = existing?.CreatedAtUtc ?? now,
            CreatedBy = existing?.CreatedBy,
            UpdatedAtUtc = now,
            Version = (existing?.Version ?? 0L) + 1L,
        };

        await _bills.UpsertAsync(tenantId, bill, admittedAt, cancellationToken).ConfigureAwait(false);

        return existing is null
            ? new ImportOutcome<Bill>.Inserted(bill, $"Imported {source.Name}.")
            : new ImportOutcome<Bill>.Updated(bill, $"Reconciled {source.Name} to modified={source.Modified}.");
    }

    /// <summary>
    /// Build a <see cref="ImportOutcome{T}.Rejected"/> for a missing required
    /// field. Only the opaque ERPNext name, the DocType, the reason code, and the
    /// offending field NAME are carried — never the field's value (ADR 0100 C9).
    /// </summary>
    private static ImportOutcome<Bill>.Rejected Reject(string externalRef, string fieldName, string ruleViolated) =>
        new(ImportFailure.Of(
            externalRef: externalRef,
            docType: DocType,
            reason: ImportRejectReason.MissingRequiredField,
            fieldName: fieldName,
            ruleViolated: ruleViolated));

    /// <summary>
    /// Map ERPNext Purchase Invoice <c>status</c> codes to canonical AP.
    ///
    /// <list type="bullet">
    /// <item><c>"Draft"</c> → <see cref="BillStatus.Draft"/></item>
    /// <item><c>"Submitted"</c>, <c>"Overdue"</c>, <c>"Return"</c>, <c>"Debit Note Issued"</c> → <see cref="BillStatus.Received"/></item>
    /// <item><c>"Partly Paid"</c>, <c>"Partly Paid and Discounted"</c> → <see cref="BillStatus.PartiallyPaid"/></item>
    /// <item><c>"Paid"</c>, <c>"Paid and Discounted"</c> → <see cref="BillStatus.Paid"/></item>
    /// <item><c>"Cancelled"</c> → <see cref="BillStatus.Voided"/></item>
    /// <item>Unknown / empty → <see cref="BillStatus.Draft"/> as the safest non-posting state.</item>
    /// </list>
    /// </summary>
    public static BillStatus MapStatus(string? erpnextStatus) =>
        erpnextStatus?.Trim() switch
        {
            "Draft" or null or "" => BillStatus.Draft,
            "Submitted" or "Overdue" or "Return" or "Debit Note Issued" => BillStatus.Received,
            "Partly Paid" or "Partly Paid and Discounted" => BillStatus.PartiallyPaid,
            "Paid" or "Paid and Discounted" => BillStatus.Paid,
            "Cancelled" => BillStatus.Voided,
            _ => BillStatus.Draft,
        };
}
