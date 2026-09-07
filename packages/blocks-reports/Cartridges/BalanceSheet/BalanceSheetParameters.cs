using Harborline.Api.Blocks.FinancialLedger.Models;

namespace Harborline.Api.Blocks.Reports.Cartridges.BalanceSheet;

/// <summary>
/// Parameters for the <see cref="BalanceSheetCartridge"/>.
/// </summary>
/// <remarks>
/// R5 — Balance Sheet cartridge. Exactly one of
/// <see cref="FiscalPeriodId"/> or <see cref="AsOfDate"/> must be
/// set — never both, never neither (parameter validation throws
/// <see cref="Exceptions.ReportParameterValidationException"/>).
/// </remarks>
public sealed record BalanceSheetParameters
{
    /// <summary>Required. Identifies the chart whose accounts are reported.</summary>
    public required ChartOfAccountsId ChartId { get; init; }

    /// <summary>
    /// Bind a fiscal period — cartridge uses <c>FiscalPeriod.EndDate</c> as the
    /// as-of date and derives provisionality from <c>FiscalPeriod.Status</c>.
    /// </summary>
    public FiscalPeriodId? FiscalPeriodId { get; init; }

    /// <summary>
    /// Explicit as-of date — caller takes responsibility for provisionality decisions.
    /// </summary>
    public System.DateOnly? AsOfDate { get; init; }

    /// <summary>
    /// When <c>true</c>, accounts with zero balance are included in the output sections.
    /// Default <c>false</c>.
    /// </summary>
    public bool IncludeZeroBalanceAccounts { get; init; } = false;

    /// <summary>
    /// When <c>true</c>, inactive (<c>GLAccount.IsActive == false</c>) accounts are
    /// included. Default <c>false</c>.
    /// </summary>
    public bool IncludeInactiveAccounts { get; init; } = false;
}
