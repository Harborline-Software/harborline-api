using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialLedger.Services;
using Harborline.Api.Blocks.Reports.Exceptions;

namespace Harborline.Api.Blocks.Reports.Cartridges.ProfitAndLoss;

/// <summary>
/// R5 — Generic entity-level Profit and Loss cartridge. Produces an
/// income statement (revenue / expenses / net profit) over a period
/// window from the GL, scoped to a chart and tenant.
/// </summary>
/// <remarks>
/// <para>
/// <b>Generic vs property-dimensioned.</b> This cartridge produces the
/// standard entity-level P&amp;L that any business type needs. The
/// property-management variant (<c>ProfitAndLossByPropertyCartridge</c>)
/// adds a property dimension on top and is a PM-pack concern; both
/// cartridges coexist and serve different consumers.
/// </para>
/// <para>
/// <b>Journal-scan approach.</b> Like <c>ProfitAndLossByPropertyCartridge</c>
/// this cartridge reads the journal snapshot directly via
/// <see cref="IJournalStore"/> to compute period-bounded activity,
/// rather than via <see cref="IGeneralLedgerReadModel"/> which provides
/// cumulative as-of balances (unsuitable for period-bounded income
/// statements without a comparative anchor).
/// </para>
/// <para>
/// <b>Sign convention.</b>
/// Revenue accounts are credit-normal: a net credit balance (raw
/// debit − credit negative) means income was earned. The cartridge
/// converts to a positive display value by negating the raw.
/// Expense accounts are debit-normal: a net debit balance (raw
/// positive) means cost was incurred. Already positive, no negation.
/// </para>
/// <para>
/// <b>Reversed-entry inclusion (BUG-002 fix).</b>
/// Only <see cref="JournalEntryStatus.Posted"/> entries contribute.
/// <c>Reversed</c> entries are excluded — they are cancelled; the
/// offsetting reversal entry (itself <c>Posted</c>) carries the
/// corrective amounts.
/// </para>
/// <para>
/// <b>Tenant isolation.</b> Per D4-C precedent (Trial Balance): the
/// caller is responsible for resolving
/// <c>ChartId → LegalEntity → TenantId</c> before calling the
/// cartridge.
/// </para>
/// </remarks>
public sealed class ProfitAndLossCartridge
    : IReportCartridge<ProfitAndLossParameters, ProfitAndLossResult>
{
    private readonly IJournalStore _journals;
    private readonly IAccountResolver _accounts;

    /// <summary>Construct bound to the journal store and account resolver.</summary>
    public ProfitAndLossCartridge(
        IJournalStore journals,
        IAccountResolver accounts)
    {
        _journals = journals ?? throw new ArgumentNullException(nameof(journals));
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
    }

    /// <inheritdoc />
    public ReportKind Kind => ReportKind.ProfitAndLoss;

    /// <inheritdoc />
    public async Task<ProfitAndLossResult> ExecuteAsync(
        ReportExecutionContext context,
        ProfitAndLossParameters parameters,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        // 1. Resolve period end. Default = context wall-clock date.
        var periodEnd = parameters.PeriodEnd
            ?? DateOnly.FromDateTime(context.AsOfUtc.UtcDateTime);

        if (parameters.PeriodStart is not null && parameters.PeriodStart.Value > periodEnd)
            throw new ReportParameterValidationException(
                nameof(parameters.PeriodStart),
                $"PeriodStart ({parameters.PeriodStart.Value}) must not be after PeriodEnd ({periodEnd}).");

        // 2. Enumerate Revenue + Expense accounts (active only; hand-off scope).
        var allAccounts = await _accounts
            .EnumerateForChartAsync(parameters.ChartId, includeInactive: false, ct)
            .ConfigureAwait(false);

        var revenueAccounts = allAccounts
            .Where(a => a.Type == GLAccountType.Revenue)
            .ToDictionary(a => a.Id);
        var expenseAccounts = allAccounts
            .Where(a => a.Type == GLAccountType.Expense)
            .ToDictionary(a => a.Id);

        // 3. Scan the journal — accumulate per-account signed balances (debit positive).
        var revenueBalances = new Dictionary<GLAccountId, decimal>();
        var expenseBalances = new Dictionary<GLAccountId, decimal>();

        foreach (var entry in _journals.Snapshot(context.TenantId))
        {
            if (entry.Status != JournalEntryStatus.Posted) continue;
            if (entry.ChartId is null || entry.ChartId.Value != parameters.ChartId) continue;
            if (entry.EntryDate > periodEnd) continue;
            if (parameters.PeriodStart is not null && entry.EntryDate < parameters.PeriodStart.Value) continue;

            foreach (var line in entry.Lines)
            {
                if (revenueAccounts.ContainsKey(line.AccountId))
                {
                    revenueBalances.TryGetValue(line.AccountId, out var running);
                    revenueBalances[line.AccountId] = running + line.Debit - line.Credit;
                }
                else if (expenseAccounts.ContainsKey(line.AccountId))
                {
                    expenseBalances.TryGetValue(line.AccountId, out var running);
                    expenseBalances[line.AccountId] = running + line.Debit - line.Credit;
                }
            }
        }

        // 4. Build ordered display lines.
        var revenueLines = BuildLines(revenueAccounts, revenueBalances,
            parameters.IncludeZeroBalanceAccounts, isRevenue: true);
        var expenseLines = BuildLines(expenseAccounts, expenseBalances,
            parameters.IncludeZeroBalanceAccounts, isRevenue: false);

        var totalRevenue  = revenueLines.Sum(l => l.Amount);
        var totalExpenses = expenseLines.Sum(l => l.Amount);

        return new ProfitAndLossResult(
            ChartId:       parameters.ChartId,
            PeriodStart:   parameters.PeriodStart,
            PeriodEnd:     periodEnd,
            RevenueLines:  revenueLines,
            ExpenseLines:  expenseLines,
            TotalRevenue:  totalRevenue,
            TotalExpenses: totalExpenses,
            NetProfit:     totalRevenue - totalExpenses);
    }

    // ──────────────────────────────────────────────────────────────────
    //  Helpers
    // ──────────────────────────────────────────────────────────────────

    private static IReadOnlyList<ProfitAndLossLine> BuildLines(
        Dictionary<GLAccountId, GLAccount> accountMap,
        Dictionary<GLAccountId, decimal> balances,
        bool includeZero,
        bool isRevenue)
    {
        var lines = new List<ProfitAndLossLine>();
        foreach (var acct in accountMap.Values
            .OrderBy(a => a.Code, StringComparer.Ordinal)
            .ThenBy(a => a.Id.ToString(), StringComparer.Ordinal))
        {
            balances.TryGetValue(acct.Id, out var raw);
            if (raw == 0m && !includeZero) continue;

            // Revenue: credit-normal → income earned = −raw → display as positive.
            // Expense: debit-normal  → cost incurred  =  raw → already positive.
            var displayAmount = isRevenue ? -raw : raw;

            lines.Add(new ProfitAndLossLine(
                AccountId:   acct.Id,
                AccountCode: acct.Code,
                AccountName: acct.Name,
                Amount:      displayAmount));
        }
        return lines;
    }
}
