using System.Collections.Generic;
using Harborline.Api.Blocks.FinancialLedger.Models;

namespace Harborline.Api.Blocks.Reports.Cartridges.BalanceSheet;

/// <summary>
/// Result of a Balance Sheet run. Implements
/// <see cref="IReportProvisionalitySource"/> so the
/// <see cref="ReportRunner"/> propagates
/// <see cref="IsProvisional"/> + <see cref="Warnings"/> into the
/// <see cref="ReportRunResult{T}"/> envelope.
/// </summary>
/// <remarks>
/// R5. All monetary values are in the chart's currency (decimal, never
/// floating-point). The accounting equation Assets = Liabilities + Equity
/// must hold; when <see cref="IsBalanced"/> is <c>false</c> the chart has
/// unposted adjustments or data issues and a warning is attached.
///
/// Sign convention follows standard double-entry:
/// <list type="bullet">
///   <item>Assets: debit-normal. Raw positive balance → asset value shown as positive.</item>
///   <item>Liabilities: credit-normal. Raw negative balance → liability shown as positive.</item>
///   <item>Equity: credit-normal. Raw negative balance → equity shown as positive.</item>
/// </list>
/// </remarks>
/// <param name="ChartId">The chart this run was scoped to.</param>
/// <param name="AsOf">The as-of date the cartridge resolved.</param>
/// <param name="PeriodId">Non-null when the run was bound to a fiscal period.</param>
/// <param name="Assets">Asset-section lines ordered by account code (ordinal).</param>
/// <param name="Liabilities">Liability-section lines ordered by account code (ordinal).</param>
/// <param name="Equity">Equity-section lines ordered by account code (ordinal).</param>
/// <param name="TotalAssets">Sum of asset values (positive = assets held).</param>
/// <param name="TotalLiabilities">Sum of liability values (positive = obligations owed).</param>
/// <param name="TotalEquity">Sum of equity values (positive = owners' interest).</param>
/// <param name="IsBalanced">
/// <c>true</c> when <see cref="TotalAssets"/> == <see cref="TotalLiabilities"/> +
/// <see cref="TotalEquity"/> within decimal precision.
/// </param>
/// <param name="IsProvisional">
/// <c>true</c> when the bound fiscal period status is <c>Open</c> or
/// <c>SoftClosed</c>.
/// </param>
/// <param name="Warnings">
/// Cartridge-attached warnings (provisionality reasons + unbalanced-chart diagnostic).
/// </param>
public sealed record BalanceSheetResult(
    ChartOfAccountsId ChartId,
    System.DateOnly AsOf,
    FiscalPeriodId? PeriodId,
    IReadOnlyList<BalanceSheetLine> Assets,
    IReadOnlyList<BalanceSheetLine> Liabilities,
    IReadOnlyList<BalanceSheetLine> Equity,
    decimal TotalAssets,
    decimal TotalLiabilities,
    decimal TotalEquity,
    bool IsBalanced,
    bool IsProvisional,
    IReadOnlyList<string> Warnings) : IReportProvisionalitySource;

/// <summary>One line within a balance sheet section.</summary>
/// <param name="AccountId">Account identifier.</param>
/// <param name="AccountCode">Human-readable code (e.g. <c>"1000"</c>).</param>
/// <param name="AccountName">Display name (e.g. <c>"Cash"</c>).</param>
/// <param name="AccountType">The account's category.</param>
/// <param name="Amount">
/// Display value — always a positive number when the account has a
/// normal-side balance. Assets: debit balance shown as-is.
/// Liabilities/Equity: credit balance negated to positive.
/// </param>
public sealed record BalanceSheetLine(
    GLAccountId AccountId,
    string AccountCode,
    string AccountName,
    GLAccountType AccountType,
    decimal Amount);
