using System.Text.Json.Nodes;

using Harborline.Api.Blocks.FinancialAr.Models;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Documents.Merge;

namespace Harborline.Api.LocalNodeHost.Data.Documents;

/// <summary>
/// The invoice→<see cref="DocumentMergeModel"/> binding step (#111 §1.2/§6 D2) — the node's own mapping,
/// lifted from the D2 keystone (<c>InvoiceIssuanceKeystoneTests.MapInvoiceToMergeModel</c>) so the D3
/// render/issue routes merge a REAL <c>#127</c> invoice through the SAME field vocabulary the keystone
/// proved: <c>field.invoiceNumber</c> / <c>field.issueDate</c> / <c>field.dueDate</c> / <c>field.customer.name</c>
/// / <c>field.subtotal</c> / <c>field.taxTotal</c> / <c>field.total</c> / <c>field.notes</c>, and a
/// <c>lineItems</c> repeating section (<c>row.description</c> / <c>row.quantity</c> / <c>row.unitPrice</c> /
/// <c>row.amount</c>). <c>customer.email</c> is intentionally OMITTED — the node has no wired email-address
/// read model yet (Party contact info lives in its own append-only collection); a template must not offer a
/// merge field this mapper cannot honestly source (§5.7 — never a silent blank).
/// </summary>
internal static class DocumentMergeMapping
{
    /// <summary>Maps a real invoice + its resolved customer display name into the merge model.</summary>
    public static DocumentMergeModel MapInvoiceToMergeModel(Invoice invoice, string? customerName)
    {
        var rows = invoice.Lines
            .Select(l => new DocumentMergeRow(l.Id.Value, new Dictionary<string, JsonNode?>
            {
                ["description"] = JsonValue.Create(l.Description),
                ["quantity"] = JsonValue.Create(l.Quantity),
                ["unitPrice"] = JsonValue.Create(l.UnitPrice),
                ["amount"] = JsonValue.Create(l.Amount),
            }))
            .ToList();

        return DocumentMergeModel.Build()
            .Field("invoiceNumber", invoice.InvoiceNumber)
            .Field("issueDate", invoice.IssueDate.ToString("yyyy-MM-dd"))
            .Field("dueDate", invoice.DueDate.ToString("yyyy-MM-dd"))
            .Field("customer.name", customerName ?? invoice.CustomerId.Value)
            .Field("subtotal", JsonValue.Create(invoice.Subtotal))
            .Field("taxTotal", JsonValue.Create(invoice.TaxTotal))
            .Field("total", JsonValue.Create(invoice.Total))
            .Field("notes", invoice.Notes)
            .Section("lineItems", rows)
            .ToModel();
    }

    /// <summary>
    /// A clearly-labelled SAMPLE invoice for the editor's live preview (design §2.2) when no real invoice
    /// id is supplied — honest interim until the #127 starter-sample-data machinery lands (design §6
    /// "ties #127 sample data" — a named follow-up, not invented fixtures pretending to be real). The
    /// shape mirrors the D2 keystone's own fixture 1:1 so the preview exercises the real repeating-region
    /// + totals binding grammar.
    /// </summary>
    public static (Invoice Invoice, string CustomerName) BuildSampleInvoice()
    {
        var invoiceId = new InvoiceId("sample-preview-invoice");
        var lines = new List<InvoiceLine>
        {
            new()
            {
                Id = InvoiceLineId.NewId(), InvoiceId = invoiceId, LineNumber = 1,
                Description = "Consulting — sample line", Quantity = 10m, UnitPrice = 150.00m, Amount = 1500.00m,
                IncomeAccountId = GLAccountId.NewId(),
            },
            new()
            {
                Id = InvoiceLineId.NewId(), InvoiceId = invoiceId, LineNumber = 2,
                Description = "Hosting (monthly) — sample line", Quantity = 1m, UnitPrice = 49.95m, Amount = 49.95m,
                IncomeAccountId = GLAccountId.NewId(),
            },
        };

        var invoice = Invoice.Create(
            tenantId: new TenantId("preview-sample"),
            chartId: ChartOfAccountsId.NewId(),
            invoiceNumber: "INV-2026-01-01-SAMPLE-0001",
            customerId: PartyId.NewId(),
            issueDate: new DateOnly(2026, 1, 1),
            dueDate: new DateOnly(2026, 1, 31),
            lines: lines,
            arAccountId: GLAccountId.NewId(),
            createdAtUtc: new Instant(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            notes: "Thank you for your business. (sample preview data)");

        // Ledger-authoritative totals INCLUDE tax — matches the F5 lesson: a template must print the
        // STORED total, never a render-time re-sum of line amounts.
        return (invoice with { TaxTotal = 149.05m, Total = 1699.00m }, "Sample Customer");
    }
}
