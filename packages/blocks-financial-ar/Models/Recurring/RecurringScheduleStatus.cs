namespace Harborline.Api.Blocks.FinancialAr.Models;

/// <summary>
/// Lifecycle state of a <see cref="RecurringInvoiceSchedule"/>.
/// Mirrors the shape of <c>Harborline.Api.Blocks.WorkOrders.Models.ScheduleStatus</c>
/// (Active/Paused/Archived) — kept local so blocks-financial-ar does not
/// take a dependency on blocks-work-orders.
/// </summary>
public enum RecurringScheduleStatus
{
    /// <summary>Generating invoices on the configured cadence.</summary>
    Active,

    /// <summary>
    /// Temporarily not generating new invoices; resume returns to Active.
    /// Bulk generation skips paused schedules and reports
    /// <see cref="Harborline.Api.Blocks.FinancialAr.Services.ScheduleGenerationOutcome.Paused"/>.
    /// </summary>
    Paused,

    /// <summary>Terminal — historical record; never resumed.</summary>
    Archived,
}
