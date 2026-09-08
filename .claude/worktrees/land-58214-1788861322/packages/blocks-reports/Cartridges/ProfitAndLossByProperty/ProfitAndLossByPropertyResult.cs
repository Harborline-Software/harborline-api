using System.Collections.Generic;
using Harborline.Api.Blocks.FinancialLedger.Models;

namespace Harborline.Api.Blocks.Reports.Cartridges.ProfitAndLossByProperty;

/// <summary>
/// P&amp;L by Property result — per-property income statement with revenue,
/// expense, and net income rows, plus portfolio totals.
/// </summary>
/// <remarks>
/// W#72 PR 5 per Stage 02 §4.15. All monetary values are in the
/// chart's currency (decimal, never floating-point). Revenue is
/// expressed as a positive number (credits net to positive). Expenses
/// are expressed as a positive number (debits net to positive). Net
/// income = Revenue - Expenses.
/// </remarks>
/// <param name="ChartId">The chart this result was computed from.</param>
/// <param name="PeriodStart">Inclusive window start used for the projection (null = open-ended from earliest entry).</param>
/// <param name="PeriodEnd">Inclusive window end used for the projection.</param>
/// <param name="ByProperty">
/// One row per property (plus an "Unassigned" row for journal lines with
/// a null <see cref="JournalEntryLine.PropertyId"/>), ordered by
/// <see cref="ProfitAndLossByPropertyRow.PropertyKey"/> (ordinal ascending,
/// with "Unassigned" sorted last).
/// </param>
/// <param name="Totals">Portfolio-level sum across all properties.</param>
public sealed record ProfitAndLossByPropertyResult(
    ChartOfAccountsId ChartId,
    System.DateOnly? PeriodStart,
    System.DateOnly PeriodEnd,
    IReadOnlyList<ProfitAndLossByPropertyRow> ByProperty,
    ProfitAndLossByPropertyTotals Totals);

/// <summary>
/// One property row in the P&amp;L by Property report — income statement
/// for a single property or the "Unassigned" bucket.
/// </summary>
/// <param name="PropertyKey">
/// Stable string key: property id string, "Unassigned" for lines with no
/// PropertyId, or "All" for the portfolio total row.
/// </param>
/// <param name="TotalRevenue">
/// Sum of revenue for this property in the window (positive = income earned).
/// </param>
/// <param name="TotalExpenses">
/// Sum of expenses for this property in the window (positive = cost incurred).
/// </param>
/// <param name="NetIncome">
/// <see cref="TotalRevenue"/> minus <see cref="TotalExpenses"/>.
/// Positive = net profit; negative = net loss.
/// </param>
/// <param name="RevenueLines">
/// Per-account revenue lines for this property, ordered by account code (ordinal).
/// </param>
/// <param name="ExpenseLines">
/// Per-account expense lines for this property, ordered by account code (ordinal).
/// </param>
public sealed record ProfitAndLossByPropertyRow(
    string PropertyKey,
    decimal TotalRevenue,
    decimal TotalExpenses,
    decimal NetIncome,
    IReadOnlyList<ProfitAndLossAccountLine> RevenueLines,
    IReadOnlyList<ProfitAndLossAccountLine> ExpenseLines);

/// <summary>
/// Portfolio-level totals across all properties.
/// </summary>
/// <param name="TotalRevenue">Sum of revenue across all properties.</param>
/// <param name="TotalExpenses">Sum of expenses across all properties.</param>
/// <param name="NetIncome">TotalRevenue minus TotalExpenses.</param>
public sealed record ProfitAndLossByPropertyTotals(
    decimal TotalRevenue,
    decimal TotalExpenses,
    decimal NetIncome);

/// <summary>
/// One account line within a property's revenue or expense section.
/// </summary>
/// <param name="AccountId">The GL account identifier.</param>
/// <param name="AccountCode">Human-readable account code, e.g. "4000".</param>
/// <param name="AccountName">Display name of the account.</param>
/// <param name="Amount">
/// Net activity in the window for this account + property combination.
/// Always a positive number — revenue credits net to positive; expense
/// debits net to positive.
/// </param>
public sealed record ProfitAndLossAccountLine(
    GLAccountId AccountId,
    string AccountCode,
    string AccountName,
    decimal Amount);
