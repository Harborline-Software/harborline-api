using Harborline.Api.Blocks.FinancialLedger.Models;

namespace Harborline.Api.Blocks.FinancialLedger.Seeds;

/// <summary>
/// Catalogue of pre-built <see cref="ChartTemplate"/> shapes. Used by
/// <see cref="Services.IChartSeedingService"/> at chart-creation time
/// to populate a fresh <see cref="ChartOfAccounts"/> with a sensible
/// starter set of <see cref="GLAccount"/> records.
/// </summary>
public static class DefaultChartTemplates
{
    /// <summary>
    /// Property-management chart shape tuned for a US single-LLC rental
    /// business with Schedule E (Form 1040) reporting. Expense rows
    /// carry Schedule E line numbers in <see cref="ChartTemplateAccount.Code"/>
    /// comments to bind them to the eventual
    /// <c>blocks-reports-tax.TaxFormLineMap</c> records (NOT seeded here
    /// — that ships with a separate <c>blocks-reports-*</c> hand-off).
    /// </summary>
    public static readonly ChartTemplate RentalRealEstate = new(
        Name: "Rental Real Estate (US, single LLC)",
        Description: "Suitable for a property LLC with rental income, operating "
                   + "expenses, and Schedule E (Form 1040) reporting needs.",
        Accounts: new ChartTemplateAccount[]
        {
            // 1xxx Assets
            new("1000", "Assets", GLAccountType.Asset, AccountSubtype.OtherAsset, IsPostable: false),
            new("1100", "Current Assets", GLAccountType.Asset, AccountSubtype.CurrentAsset, ParentCode: "1000", IsPostable: false),
            new("1110", "Operating Bank Account", GLAccountType.Asset, AccountSubtype.BankAccount, ParentCode: "1100"),
            new("1120", "Security Deposit Holding (Bank)", GLAccountType.Asset, AccountSubtype.BankAccount, ParentCode: "1100"),
            new("1130", "Accounts Receivable", GLAccountType.Asset, AccountSubtype.AccountsReceivable, ParentCode: "1100"),
            new("1500", "Fixed Assets", GLAccountType.Asset, AccountSubtype.FixedAsset, ParentCode: "1000", IsPostable: false),
            new("1510", "Buildings", GLAccountType.Asset, AccountSubtype.FixedAsset, ParentCode: "1500"),
            new("1520", "Land", GLAccountType.Asset, AccountSubtype.FixedAsset, ParentCode: "1500"),
            new("1530", "Equipment", GLAccountType.Asset, AccountSubtype.FixedAsset, ParentCode: "1500"),
            new("1590", "Accumulated Depreciation", GLAccountType.Asset, AccountSubtype.AccumulatedDepreciation, ParentCode: "1500"),

            // 2xxx Liabilities
            new("2000", "Liabilities", GLAccountType.Liability, AccountSubtype.OtherLiability, IsPostable: false),
            new("2100", "Current Liabilities", GLAccountType.Liability, AccountSubtype.CurrentLiability, ParentCode: "2000", IsPostable: false),
            new("2110", "Accounts Payable", GLAccountType.Liability, AccountSubtype.AccountsPayable, ParentCode: "2100"),
            new("2120", "Security Deposits Held", GLAccountType.Liability, AccountSubtype.CurrentLiability, ParentCode: "2100"),
            new("2130", "Sales Tax Payable", GLAccountType.Liability, AccountSubtype.TaxesPayable, ParentCode: "2100"),
            new("2500", "Long-Term Liabilities", GLAccountType.Liability, AccountSubtype.LongTermLiability, ParentCode: "2000", IsPostable: false),
            new("2510", "Mortgages Payable", GLAccountType.Liability, AccountSubtype.LongTermLiability, ParentCode: "2500"),

            // 3xxx Equity
            new("3000", "Equity", GLAccountType.Equity, AccountSubtype.OwnersEquity, IsPostable: false),
            new("3100", "Owner's Capital", GLAccountType.Equity, AccountSubtype.OwnersEquity, ParentCode: "3000"),
            new("3200", "Owner's Drawings", GLAccountType.Equity, AccountSubtype.Drawings, ParentCode: "3000"),
            new("3900", "Retained Earnings", GLAccountType.Equity, AccountSubtype.RetainedEarnings, ParentCode: "3000"),

            // 4xxx Revenue
            new("4000", "Revenue", GLAccountType.Revenue, AccountSubtype.OperatingIncome, IsPostable: false),
            new("4100", "Rental Income", GLAccountType.Revenue, AccountSubtype.OperatingIncome, ParentCode: "4000"),
            new("4200", "Late Fee Income", GLAccountType.Revenue, AccountSubtype.OperatingIncome, ParentCode: "4000"),
            new("4900", "Other Income", GLAccountType.Revenue, AccountSubtype.OtherIncome, ParentCode: "4000"),

            // 5xxx-7xxx Expenses (Schedule E line-mapped — see ScheduleELineMap below)
            new("5000", "Expenses", GLAccountType.Expense, AccountSubtype.OperatingExpense, IsPostable: false),
            new("5100", "Advertising", GLAccountType.Expense, AccountSubtype.OperatingExpense, ParentCode: "5000"),                  // Schedule E Line 5
            new("5200", "Cleaning and Maintenance", GLAccountType.Expense, AccountSubtype.OperatingExpense, ParentCode: "5000"),    // Line 7
            new("5300", "Insurance", GLAccountType.Expense, AccountSubtype.OperatingExpense, ParentCode: "5000"),                   // Line 9
            new("5400", "Legal and Professional Fees", GLAccountType.Expense, AccountSubtype.OperatingExpense, ParentCode: "5000"), // Line 10
            new("5500", "Management Fees", GLAccountType.Expense, AccountSubtype.OperatingExpense, ParentCode: "5000"),             // Line 11
            new("5600", "Repairs", GLAccountType.Expense, AccountSubtype.OperatingExpense, ParentCode: "5000"),                     // Line 14
            new("5700", "Supplies", GLAccountType.Expense, AccountSubtype.OperatingExpense, ParentCode: "5000"),                    // Line 15
            new("5800", "Utilities", GLAccountType.Expense, AccountSubtype.OperatingExpense, ParentCode: "5000"),                   // Line 17
            new("6100", "Property Tax", GLAccountType.Expense, AccountSubtype.OperatingExpense, ParentCode: "5000"),                // Line 16
            new("7110", "Mortgage Interest", GLAccountType.Expense, AccountSubtype.InterestExpense, ParentCode: "5000"),            // Line 12
            new("7200", "Depreciation Expense", GLAccountType.Expense, AccountSubtype.DepreciationExpense, ParentCode: "5000"),     // Line 18
        });

    /// <summary>
    /// Schedule E (Form 1040) line → <see cref="ChartTemplateAccount.Code"/>
    /// binding for the <see cref="RentalRealEstate"/> template. Used by
    /// the future <c>blocks-reports-tax</c> hand-off to seed
    /// <c>TaxFormLineMap</c> records and by the package's
    /// DefaultChartTemplatesTests to verify coverage.
    /// </summary>
    /// <remarks>
    /// The template intentionally does NOT cover every Schedule E line
    /// (lines 6 Auto / 8 Commissions / 13 Other are omitted as
    /// blank-rare for a property LLC). The test verifies the listed
    /// lines map to a present account, not universal Schedule E
    /// coverage.
    /// </remarks>
    public static readonly IReadOnlyDictionary<int, string> RentalRealEstateScheduleELineMap =
        new Dictionary<int, string>
        {
            [5]  = "5100", // Advertising
            [7]  = "5200", // Cleaning and Maintenance
            [9]  = "5300", // Insurance
            [10] = "5400", // Legal and Professional Fees
            [11] = "5500", // Management Fees
            [12] = "7110", // Mortgage Interest
            [14] = "5600", // Repairs
            [15] = "5700", // Supplies
            [16] = "6100", // Property Tax
            [17] = "5800", // Utilities
            [18] = "7200", // Depreciation
        };

    /// <summary>
    /// The non-equity accounts (assets, liabilities, revenue, Schedule E
    /// expenses) shared by every per-entity chart in a property-management
    /// group. Per ADR 0104 §4.2 ruling #5, the multi-member-LLC and S-corp
    /// equity variants differ from <see cref="RentalRealEstate"/> ONLY in
    /// their equity block — "everything except equity is the standard
    /// per-entity financials." Derived from <see cref="RentalRealEstate"/>
    /// so the shared rows have a single source of truth.
    /// </summary>
    private static readonly IReadOnlyList<ChartTemplateAccount> StandardNonEquityAccounts =
        RentalRealEstate.Accounts.Where(a => a.Type != GLAccountType.Equity).ToArray();

    /// <summary>
    /// Equity-chart variant for a management company taxed as an S-corp
    /// (ADR 0104 §4.2). Identical to <see cref="RentalRealEstate"/> except the
    /// equity block: an S-corp has stock + additional paid-in capital +
    /// retained earnings + shareholder distributions, NOT owner's
    /// capital/drawings. Per ruling #5 only the equity block differs; the
    /// asset/liability/revenue/expense structure is the standard per-entity
    /// financials.
    /// </summary>
    public static readonly ChartTemplate ScorpManagementCo = new(
        Name: "S-Corp Management Company (US)",
        Description: "Suitable for a management company taxed as an S-corporation: "
                   + "common stock, additional paid-in capital, retained earnings, and "
                   + "shareholder distributions in place of owner's capital/drawings.",
        Accounts: StandardNonEquityAccounts.Concat(new ChartTemplateAccount[]
        {
            new("3000", "Shareholders' Equity", GLAccountType.Equity, AccountSubtype.OwnersEquity, IsPostable: false),
            new("3100", "Common Stock", GLAccountType.Equity, AccountSubtype.CommonStock, ParentCode: "3000"),
            new("3200", "Additional Paid-In Capital", GLAccountType.Equity, AccountSubtype.PaidInCapital, ParentCode: "3000"),
            new("3300", "Shareholder Distributions", GLAccountType.Equity, AccountSubtype.ShareholderDistributions, ParentCode: "3000"),
            new("3900", "Retained Earnings", GLAccountType.Equity, AccountSubtype.RetainedEarnings, ParentCode: "3000"),
        }).ToArray());

    /// <summary>
    /// Equity-chart variant for a multi-member LLC (ADR 0104 §4.1),
    /// parameterized by the member list at seed time. Identical to
    /// <see cref="RentalRealEstate"/> except the equity block, which expands
    /// each member into four sub-accounts (capital, contributions,
    /// distributions, allocated K-1 income) under a Members' Equity header,
    /// plus a shared undistributed retained-earnings account.
    /// </summary>
    /// <remarks>
    /// The per-member GL accounts are a PRESENTATION convenience; the
    /// allocation-bearing fact rides the <c>JournalEntryLine.MemberId</c>
    /// dimension (ADR 0104 §4.3, financial F-4), NOT the account number. The
    /// Wave-3 K-1 allocation helper
    /// keys off that dimension; this template only guarantees the chart SHAPE
    /// exists for per-member ending-capital display.
    /// </remarks>
    /// <param name="memberNames">
    /// Ordered member display names (e.g. "Member A", "Member B"). Each expands
    /// to four equity sub-accounts. Must be non-empty; capped at 8 members so
    /// the generated codes (3100..3800) do not collide with 3900 Retained
    /// Earnings.
    /// </param>
    public static ChartTemplate MultiMemberLlc(IReadOnlyList<string> memberNames)
    {
        if (memberNames is null || memberNames.Count == 0)
        {
            throw new ArgumentException(
                "A multi-member LLC chart requires at least one member.", nameof(memberNames));
        }
        if (memberNames.Count > 8)
        {
            throw new ArgumentException(
                $"At most 8 members are supported (got {memberNames.Count}); the per-member equity "
                + "codes 3100..3800 would otherwise collide with 3900 Retained Earnings.",
                nameof(memberNames));
        }

        var equity = new List<ChartTemplateAccount>
        {
            new("3000", "Members' Equity", GLAccountType.Equity, AccountSubtype.OwnersEquity, IsPostable: false),
        };
        for (var i = 0; i < memberNames.Count; i++)
        {
            var name = memberNames[i];
            var b = 3100 + (i * 100);
            equity.Add(new($"{b}", $"{name} — Capital", GLAccountType.Equity, AccountSubtype.MemberCapital, ParentCode: "3000"));
            equity.Add(new($"{b + 10}", $"{name} — Contributions", GLAccountType.Equity, AccountSubtype.MemberCapital, ParentCode: "3000"));
            equity.Add(new($"{b + 20}", $"{name} — Distributions", GLAccountType.Equity, AccountSubtype.MemberDistributions, ParentCode: "3000"));
            equity.Add(new($"{b + 30}", $"{name} — Allocated Income (K-1)", GLAccountType.Equity, AccountSubtype.MemberCapital, ParentCode: "3000"));
        }
        equity.Add(new("3900", "Retained Earnings / Undistributed", GLAccountType.Equity, AccountSubtype.RetainedEarnings, ParentCode: "3000"));

        var memberLabel = memberNames.Count == 1 ? "1 member" : $"{memberNames.Count} members";
        return new ChartTemplate(
            Name: $"Multi-Member LLC (US, {memberLabel})",
            Description: "Suitable for a multi-member rental LLC: per-member capital, "
                       + "contributions, distributions, and allocated K-1 income sub-accounts "
                       + "under a Members' Equity header, with Schedule E expenses.",
            Accounts: StandardNonEquityAccounts.Concat(equity).ToArray());
    }
}
