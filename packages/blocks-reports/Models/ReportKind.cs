namespace Harborline.Api.Blocks.Reports;

/// <summary>
/// Discriminator for the kinds of read-side reports the cluster can
/// run. Each member maps to a cartridge implementation registered in
/// <see cref="ReportCartridgeRegistry"/>. Adding a new cartridge in a
/// follow-on hand-off = add a member here + the
/// <see cref="ReportKindExtensions.ToKebab"/> case + the cartridge
/// itself.
/// </summary>
public enum ReportKind
{
    /// <summary>Standard accounting trial balance. PR 2.</summary>
    TrialBalance,

    /// <summary>AR aging summary (0/30/60/90+ buckets). PR 3.</summary>
    ArAgingSummary,

    /// <summary>AP aging summary (0/30/60/90+ buckets). PR 4.</summary>
    ApAgingSummary,

    /// <summary>Profit-and-loss by property dimension. PR 5.</summary>
    ProfitAndLossByProperty,

    /// <summary>Rent roll v2 (current + projected + aging + vacancy). PR 6.</summary>
    RentRoll,

    /// <summary>Balance sheet (assets / liabilities / equity as of a date). R5.</summary>
    BalanceSheet,

    /// <summary>Generic entity-level profit-and-loss (revenue / expenses over a period). R5.</summary>
    ProfitAndLoss,

    // Reserved for follow-on hand-offs (do NOT remove this comment):
    //   CashFlow, Statement, ScheduleE,
    //   Form1099Nec, Form1099Misc, WorkOrderSummary, MaintenanceBacklog,
    //   LeaseExpiration, Vacancy, InvoicePdf, ReceiptPdf, QuotePdf, BillPdf
}

/// <summary>Extension helpers for <see cref="ReportKind"/>.</summary>
public static class ReportKindExtensions
{
    /// <summary>Lowercase kebab-case identifier for URLs / config keys / log fields.</summary>
    public static string ToKebab(this ReportKind kind) => kind switch
    {
        ReportKind.TrialBalance            => "trial-balance",
        ReportKind.ArAgingSummary          => "ar-aging-summary",
        ReportKind.ApAgingSummary          => "ap-aging-summary",
        ReportKind.ProfitAndLossByProperty => "profit-and-loss-by-property",
        ReportKind.RentRoll                => "rent-roll",
        ReportKind.BalanceSheet            => "balance-sheet",
        ReportKind.ProfitAndLoss           => "profit-and-loss",
        _ => throw new System.InvalidOperationException($"Unmapped ReportKind: {kind}"),
    };
}
