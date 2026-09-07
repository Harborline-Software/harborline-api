namespace Harborline.Api.Blocks.FinancialPayments.Models;

/// <summary>
/// Direction of cash movement relative to the business entity.
/// </summary>
public enum PaymentDirection
{
    /// <summary>
    /// Money received from a customer. Applied to <c>Invoice</c> targets only.
    /// Direction-matching invariant: Inbound ↔ Invoice (enforced by <c>IPaymentApplicationService</c>).
    /// </summary>
    Inbound = 1,

    /// <summary>
    /// Money sent to a vendor. Applied to <c>Bill</c> targets only.
    /// Direction-matching invariant: Outbound ↔ Bill (enforced by <c>IPaymentApplicationService</c>).
    /// </summary>
    Outbound = 2,
}
