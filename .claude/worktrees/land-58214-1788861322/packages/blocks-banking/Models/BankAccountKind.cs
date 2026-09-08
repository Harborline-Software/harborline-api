namespace Harborline.Api.Blocks.Banking.Models;

/// <summary>
/// The kind of financial account a <see cref="BankAccount"/> represents.
/// Per ADR 0112 Part 1 §1 — account model.
/// </summary>
public enum BankAccountKind
{
    /// <summary>Standard bank / checking / savings account (debit-normal).</summary>
    Bank,

    /// <summary>Credit-card account (credit-normal; statement balance is owed to lender).</summary>
    CreditCard,

    /// <summary>Petty-cash or cash-in-hand account (debit-normal, no institution).</summary>
    Cash,
}
