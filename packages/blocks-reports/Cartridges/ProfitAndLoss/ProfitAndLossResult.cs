using System.Collections.Generic;
using Harborline.Api.Blocks.FinancialLedger.Models;

namespace Harborline.Api.Blocks.Reports.Cartridges.ProfitAndLoss;

/// <summary>
/// Result of a generic entity-level Profit and Loss run.
/// </summary>
/// <remarks>
/// R5. All monetary values are in the chart's currency (decimal, never
/// floating-point). Revenue is expressed as a positive number (credits
/// net to positive). Expenses are expressed as a positive number
/// (debits net to positive). Net profit = Revenue - Expenses.
///
/// Unlike <c>ProfitAndLossByPropertyResult</c> this cartridge is
/// entity-level — it aggregates all revenue and expense activity for
/// the chart without a property dimension.
/// </remarks>
/// <param name="ChartId">The chart this result was computed from.</param>
/// <param name="PeriodStart">Inclusive window start (null = open-ended from earliest entry).</param>
/// <param name="PeriodEnd">Inclusive window end used for the projection.</param>
/// <param name="RevenueLines">
/// Per-account revenue lines ordered by account code (ordinal ascending).
/// </param>
/// <param name="ExpenseLines">
/// Per-account expense lines ordered by account code (ordinal ascending).
/// </param>
/// <param name="TotalRevenue">Sum of revenue across all lines (positive = income earned).</param>
/// <param name="TotalExpenses">Sum of expenses across all lines (positive = cost incurred).</param>
/// <param name="NetProfit">
/// <see cref="TotalRevenue"/> minus <see cref="TotalExpenses"/>.
/// Positive = net profit; negative = net loss.
/// </param>
public sealed record ProfitAndLossResult(
    ChartOfAccountsId ChartId,
    System.DateOnly? PeriodStart,
    System.DateOnly PeriodEnd,
    IReadOnlyList<ProfitAndLossLine> RevenueLines,
    IReadOnlyList<ProfitAndLossLine> ExpenseLines,
    decimal TotalRevenue,
    decimal TotalExpenses,
    decimal NetProfit);

/// <summary>One account line in the P&amp;L report.</summary>
/// <param name="AccountId">The GL account identifier.</param>
/// <param name="AccountCode">Human-readable account code, e.g. "4000".</param>
/// <param name="AccountName">Display name of the account.</param>
/// <param name="Amount">
/// Net activity in the window for this account.
/// Always a positive number — revenue credits net to positive;
/// expense debits net to positive.
/// </param>
public sealed record ProfitAndLossLine(
    GLAccountId AccountId,
    string AccountCode,
    string AccountName,
    decimal Amount);
