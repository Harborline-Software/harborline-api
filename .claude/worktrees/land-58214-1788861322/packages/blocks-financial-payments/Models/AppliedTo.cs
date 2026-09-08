namespace Harborline.Api.Blocks.FinancialPayments.Models;

/// <summary>
/// Discriminates what document type a <see cref="PaymentApplication"/> targets.
/// </summary>
public enum AppliedTo
{
    /// <summary>
    /// Targets an <c>Invoice</c> (AR side). Only valid for <see cref="PaymentDirection.Inbound"/> payments.
    /// </summary>
    Invoice = 1,

    /// <summary>
    /// Targets a <c>Bill</c> (AP side). Only valid for <see cref="PaymentDirection.Outbound"/> payments.
    /// </summary>
    Bill = 2,
}
