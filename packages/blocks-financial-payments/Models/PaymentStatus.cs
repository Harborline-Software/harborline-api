namespace Harborline.Api.Blocks.FinancialPayments.Models;

/// <summary>
/// Lifecycle state of a <see cref="Payment"/>.
/// </summary>
public enum PaymentStatus
{
    /// <summary>Created but not yet cleared against the bank. No GL entry exists.</summary>
    Draft = 1,

    /// <summary>Cleared (GL entry posted). No applications yet — full amount is unapplied.</summary>
    Unapplied = 2,

    /// <summary>One or more applications exist but the full amount has not been applied.</summary>
    PartiallyApplied = 3,

    /// <summary>All cleared funds have been applied. <c>UnappliedAmount == 0</c>.</summary>
    Applied = 4,

    /// <summary>
    /// Cleared payment returned by the bank (e.g., NSF check). Reversing GL entry
    /// posted; all prior applications reversed.
    /// </summary>
    Bounced = 5,

    /// <summary>Draft voided before clearing. No GL entry; terminal state.</summary>
    Voided = 6,
}

/// <summary>Extension helpers for <see cref="PaymentStatus"/>.</summary>
public static class PaymentStatusExtensions
{
    /// <summary>Returns true for states where the payment is still active (not terminal).</summary>
    public static bool IsActive(this PaymentStatus status) =>
        status is PaymentStatus.Draft
            or PaymentStatus.Unapplied
            or PaymentStatus.PartiallyApplied
            or PaymentStatus.Applied;

    /// <summary>Returns true for terminal states that block further mutations.</summary>
    public static bool IsTerminal(this PaymentStatus status) =>
        status is PaymentStatus.Bounced or PaymentStatus.Voided;
}
