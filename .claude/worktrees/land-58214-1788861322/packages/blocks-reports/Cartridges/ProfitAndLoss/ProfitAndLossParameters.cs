using Harborline.Api.Blocks.FinancialLedger.Models;

namespace Harborline.Api.Blocks.Reports.Cartridges.ProfitAndLoss;

/// <summary>
/// Parameters for the <see cref="ProfitAndLossCartridge"/>.
/// </summary>
/// <remarks>
/// R5 — Generic entity-level profit-and-loss cartridge. Either
/// <see cref="PeriodStart"/> + <see cref="PeriodEnd"/> define the
/// income-statement window; when null the cartridge uses the full posted
/// history up to and including the context wall-clock date.
///
/// This is the generic core P&amp;L for any business type. For the
/// property-management variant (revenue/expense by property dimension)
/// see <c>ProfitAndLossByPropertyCartridge</c>.
/// </remarks>
public sealed record ProfitAndLossParameters
{
    /// <summary>The chart of accounts to report on. Required.</summary>
    public required ChartOfAccountsId ChartId { get; init; }

    /// <summary>
    /// Inclusive start of the P&amp;L period window. When null the window
    /// opens from the earliest posted entry.
    /// </summary>
    public System.DateOnly? PeriodStart { get; init; }

    /// <summary>
    /// Inclusive end of the P&amp;L period window. When null the cartridge
    /// defaults to the context's <see cref="ReportExecutionContext.AsOfUtc"/>
    /// date in UTC, matching the runner's wall-clock.
    /// </summary>
    public System.DateOnly? PeriodEnd { get; init; }

    /// <summary>
    /// When <c>true</c>, accounts with zero net activity in the window
    /// are included in the account lines. Default <c>false</c>.
    /// </summary>
    public bool IncludeZeroBalanceAccounts { get; init; } = false;
}
