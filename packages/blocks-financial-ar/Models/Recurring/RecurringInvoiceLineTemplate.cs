using Harborline.Api.Blocks.FinancialLedger.Models;

namespace Harborline.Api.Blocks.FinancialAr.Models;

/// <summary>
/// Template for a single line item to be stamped onto each generated
/// <see cref="Invoice"/>. Immutable — changing the template on a live
/// schedule only affects future generated invoices; past ones are
/// already posted.
/// </summary>
/// <param name="Description">Free-text description rendered on the invoice (e.g. "Monthly service fee").</param>
/// <param name="Quantity">Quantity (positive, decimal).</param>
/// <param name="UnitPrice">Unit price in invoice currency, major units.</param>
/// <param name="IncomeAccountId">Credit account — typically an Income / OperatingIncome subtype.</param>
/// <param name="TaxCodeId">Opaque FK into the tax cluster. <see langword="null"/> = no tax.</param>
/// <param name="PropertyId">Optional cost-center handle.</param>
public sealed record RecurringInvoiceLineTemplate(
    string Description,
    decimal Quantity,
    decimal UnitPrice,
    GLAccountId IncomeAccountId,
    string? TaxCodeId = null,
    string? PropertyId = null);
