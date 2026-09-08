namespace Harborline.Api.Blocks.FinancialPayments.Models;

/// <summary>
/// Mechanism by which the payment was transacted.
/// </summary>
public enum PaymentMethod
{
    /// <summary>Physical currency handed over at point of collection.</summary>
    Cash = 1,

    /// <summary>Paper check issued or received.</summary>
    Check = 2,

    /// <summary>Automated Clearing House electronic transfer.</summary>
    ACH = 3,

    /// <summary>Same-day or international wire transfer.</summary>
    Wire = 4,

    /// <summary>Credit or debit card charge.</summary>
    Card = 5,

    /// <summary>Digital wallet platform (e.g., Venmo, Zelle, PayPal).</summary>
    DigitalWallet = 6,

    /// <summary>Any other method not covered above; include detail in <c>Payment.Notes</c>.</summary>
    Other = 99,
}
