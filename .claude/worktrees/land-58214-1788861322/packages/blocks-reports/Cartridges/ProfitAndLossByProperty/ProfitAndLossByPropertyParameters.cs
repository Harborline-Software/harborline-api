using Harborline.Api.Blocks.FinancialLedger.Models;

namespace Harborline.Api.Blocks.Reports.Cartridges.ProfitAndLossByProperty;

/// <summary>
/// Parameters for the <see cref="ProfitAndLossByPropertyCartridge"/>.
/// </summary>
/// <remarks>
/// W#72 PR 5 — P&amp;L by Property cartridge per Stage 02 §4.15.
/// Either <see cref="PeriodStart"/> + <see cref="PeriodEnd"/> define the
/// income-statement window; when null the cartridge uses the chart's full
/// posted-entry history up to and including the context as-of date.
/// </remarks>
public sealed record ProfitAndLossByPropertyParameters
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
    /// Optional filter — only include rows for these property ids.
    /// When null all properties (including Unassigned) are included.
    /// </summary>
    public System.Collections.Generic.IReadOnlyList<string>? PropertyIds { get; init; }

    /// <summary>
    /// When <c>true</c>, accounts with zero net activity in the window
    /// are included in the account breakdown. Default <c>false</c>.
    /// </summary>
    public bool IncludeZeroBalanceAccounts { get; init; } = false;
}
