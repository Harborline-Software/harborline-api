namespace Harborline.Api.Blocks.FinancialLedger.Models;

/// <summary>
/// Sub-classification under <see cref="GLAccountType"/> per Stage 02
/// <c>blocks-financial-schema-design.md</c> §3.1. Subtype drives
/// presentation grouping on financial statements (Balance Sheet,
/// Income Statement) without changing the high-level
/// debit/credit normal balance derived from the parent
/// <see cref="GLAccountType"/>.
/// </summary>
public enum AccountSubtype
{
    // Assets
    CurrentAsset,
    FixedAsset,
    BankAccount,
    AccountsReceivable,
    InventoryAsset,
    AccumulatedDepreciation,
    OtherAsset,

    // Liabilities
    CurrentLiability,
    AccountsPayable,
    LongTermLiability,
    TaxesPayable,
    OtherLiability,

    // Equity
    OwnersEquity,
    RetainedEarnings,
    Drawings,

    // Equity — multi-member LLC capital (ADR 0104 §4.1)
    MemberCapital,
    MemberDistributions,

    // Equity — S-corp management-company variant (ADR 0104 §4.2)
    CommonStock,
    PaidInCapital,
    ShareholderDistributions,

    // Income
    OperatingIncome,
    OtherIncome,

    // Expense
    OperatingExpense,
    CostOfGoodsSold,
    InterestExpense,
    DepreciationExpense,
    OtherExpense,

    // Inter-company — settlement/elimination subtypes (ADR 0104 §4; ADR 0105 §3.2).
    // Normal balance still derives from the parent GLAccountType (asset/liability/
    // equity), NOT from these subtypes; they exist so the consolidation elimination
    // ruleset can pair due-from/due-to and investment/equity legs across charts.
    IntercompanyReceivable,      // asset leg on the creditor entity (trade)
    IntercompanyPayable,         // liability leg on the debtor entity (trade)
    InvestmentInSubsidiary,      // asset leg on the parent entity (equity-method investment)
    IntercompanyEquity,          // equity leg on the subsidiary that the parent's investment offsets
    IntercompanyLoanReceivable,  // asset leg on the lender entity (intercompany note)
    IntercompanyLoanPayable,     // liability leg on the borrower entity (intercompany note)
}
