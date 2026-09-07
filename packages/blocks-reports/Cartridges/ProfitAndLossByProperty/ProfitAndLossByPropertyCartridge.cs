using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialLedger.Services;
using Harborline.Api.Blocks.Reports.Exceptions;

namespace Harborline.Api.Blocks.Reports.Cartridges.ProfitAndLossByProperty;

/// <summary>
/// W#72 PR 5 — P&amp;L by Property cartridge per Stage 02 §4.15.
/// Projects posted journal entries in the period window against Revenue
/// and Expense accounts, rolling up per <see cref="JournalEntryLine.PropertyId"/>
/// to produce a property-level income statement.
/// </summary>
/// <remarks>
/// <para>
/// <b>Journal-scan approach.</b> Because <see cref="IGeneralLedgerReadModel"/>
/// aggregates per-account totals without the property dimension, this
/// cartridge reads the journal snapshot directly via
/// <see cref="IJournalStore"/> — the same read-only scan used by
/// <see cref="InMemoryGeneralLedgerReadModel"/>. This is consistent with
/// the hand-off note: "other read-side consumers (AR/AP Aging, P&amp;L) compose
/// their own projections on top of IJournalStore directly."
/// </para>
/// <para>
/// <b>Revenue convention.</b> Revenue accounts are credit-normal; a net
/// credit balance (raw signed value negative) means income was earned.
/// The cartridge converts to a positive display value.
/// </para>
/// <para>
/// <b>Expense convention.</b> Expense accounts are debit-normal; a net
/// debit balance (raw signed value positive) means cost was incurred.
/// The cartridge exposes this as a positive display value.
/// </para>
/// <para>
/// <b>Read-side discipline.</b> No writes, no event publication.
/// </para>
/// <para>
/// <b>Tenant isolation.</b> Per D4-C precedent from Trial Balance: the
/// caller is responsible for resolving <c>ChartId → LegalEntity → TenantId</c>
/// before calling the cartridge.
/// </para>
/// </remarks>
public sealed class ProfitAndLossByPropertyCartridge
    : IReportCartridge<ProfitAndLossByPropertyParameters, ProfitAndLossByPropertyResult>
{
    private readonly IJournalStore _journals;
    private readonly IAccountResolver _accounts;

    /// <summary>Construct bound to the journal store and account resolver.</summary>
    public ProfitAndLossByPropertyCartridge(
        IJournalStore journals,
        IAccountResolver accounts)
    {
        _journals = journals ?? throw new ArgumentNullException(nameof(journals));
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
    }

    /// <inheritdoc />
    public ReportKind Kind => ReportKind.ProfitAndLossByProperty;

    /// <inheritdoc />
    public async Task<ProfitAndLossByPropertyResult> ExecuteAsync(
        ReportExecutionContext context,
        ProfitAndLossByPropertyParameters parameters,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        // 1. Parameter validation — PeriodStart must be <= PeriodEnd when both set.
        var periodEnd = parameters.PeriodEnd
            ?? DateOnly.FromDateTime(context.AsOfUtc.UtcDateTime);

        if (parameters.PeriodStart is not null && parameters.PeriodStart.Value > periodEnd)
            throw new ReportParameterValidationException(
                nameof(parameters.PeriodStart),
                $"PeriodStart ({parameters.PeriodStart.Value}) must not be after PeriodEnd ({periodEnd}).");

        // 2. Enumerate Revenue + Expense accounts for the chart (active only unless
        //    the caller explicitly enables inactive — parameters does not expose
        //    IncludeInactiveAccounts; we always enumerate active to match hand-off scope).
        var allAccounts = await _accounts
            .EnumerateForChartAsync(parameters.ChartId, includeInactive: false, ct)
            .ConfigureAwait(false);

        // Build a fast lookup: accountId → GLAccount (Revenue/Expense only).
        var revenueAccounts = allAccounts
            .Where(a => a.Type == GLAccountType.Revenue)
            .ToDictionary(a => a.Id);
        var expenseAccounts = allAccounts
            .Where(a => a.Type == GLAccountType.Expense)
            .ToDictionary(a => a.Id);

        // 3. Scan the journal — collect per-(accountId, propertyKey) signed balances.
        //    Revenue raw = debit - credit; net credit → negative raw → income earned.
        //    Expense raw = debit - credit; net debit → positive raw → cost incurred.
        //
        //    Key: (accountId, propertyKey)
        //    Value: signed balance (debit positive)
        var revenueBalances = new Dictionary<(GLAccountId, string), decimal>();
        var expenseBalances = new Dictionary<(GLAccountId, string), decimal>();

        foreach (var entry in _journals.Snapshot(context.TenantId))
        {
            if (entry.Status != JournalEntryStatus.Posted) continue;
            if (entry.ChartId is null || entry.ChartId.Value != parameters.ChartId) continue;
            if (entry.EntryDate > periodEnd) continue;
            if (parameters.PeriodStart is not null && entry.EntryDate < parameters.PeriodStart.Value) continue;

            foreach (var line in entry.Lines)
            {
                var propKey = line.PropertyId?.Value ?? "Unassigned";

                if (revenueAccounts.ContainsKey(line.AccountId))
                {
                    var k = (line.AccountId, propKey);
                    revenueBalances.TryGetValue(k, out var running);
                    revenueBalances[k] = running + line.Debit - line.Credit;
                }
                else if (expenseAccounts.ContainsKey(line.AccountId))
                {
                    var k = (line.AccountId, propKey);
                    expenseBalances.TryGetValue(k, out var running);
                    expenseBalances[k] = running + line.Debit - line.Credit;
                }
            }
        }

        // 4. Collect the set of property keys that appear in either revenue or expense.
        var allPropertyKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, propKey) in revenueBalances.Keys) allPropertyKeys.Add(propKey);
        foreach (var (_, propKey) in expenseBalances.Keys) allPropertyKeys.Add(propKey);

        // 5. Apply optional property filter.
        IEnumerable<string> filteredKeys = allPropertyKeys;
        if (parameters.PropertyIds is { Count: > 0 })
        {
            var allowed = new HashSet<string>(parameters.PropertyIds, StringComparer.Ordinal);
            filteredKeys = allPropertyKeys.Where(k => allowed.Contains(k));
        }

        // 6. Build per-property rows.
        var propertyRows = new List<ProfitAndLossByPropertyRow>();
        foreach (var propKey in filteredKeys.OrderBy(k => k == "Unassigned" ? 1 : 0).ThenBy(k => k, StringComparer.Ordinal))
        {
            var revLines = BuildAccountLines(propKey, revenueBalances, revenueAccounts,
                parameters.IncludeZeroBalanceAccounts, isRevenue: true);
            var expLines = BuildAccountLines(propKey, expenseBalances, expenseAccounts,
                parameters.IncludeZeroBalanceAccounts, isRevenue: false);

            var totalRev = revLines.Sum(l => l.Amount);
            var totalExp = expLines.Sum(l => l.Amount);

            propertyRows.Add(new ProfitAndLossByPropertyRow(
                PropertyKey: propKey,
                TotalRevenue: totalRev,
                TotalExpenses: totalExp,
                NetIncome: totalRev - totalExp,
                RevenueLines: revLines,
                ExpenseLines: expLines));
        }

        // 7. Portfolio totals — sum over property rows so totals are always consistent.
        var portfolioRevenue = propertyRows.Sum(r => r.TotalRevenue);
        var portfolioExpenses = propertyRows.Sum(r => r.TotalExpenses);
        var totals = new ProfitAndLossByPropertyTotals(
            TotalRevenue: portfolioRevenue,
            TotalExpenses: portfolioExpenses,
            NetIncome: portfolioRevenue - portfolioExpenses);

        return new ProfitAndLossByPropertyResult(
            ChartId: parameters.ChartId,
            PeriodStart: parameters.PeriodStart,
            PeriodEnd: periodEnd,
            ByProperty: propertyRows,
            Totals: totals);
    }

    // ──────────────────────────────────────────────────────────────────
    //  Helpers
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Build per-account display lines for a single property key.
    /// <paramref name="isRevenue"/> determines sign convention:
    ///   Revenue: raw is debit-positive; income = -raw → negate to get positive display value.
    ///   Expense: raw is debit-positive; cost = raw → already positive display value.
    /// </summary>
    private static IReadOnlyList<ProfitAndLossAccountLine> BuildAccountLines(
        string propKey,
        Dictionary<(GLAccountId, string), decimal> balances,
        Dictionary<GLAccountId, GLAccount> accountMap,
        bool includeZero,
        bool isRevenue)
    {
        var lines = new List<ProfitAndLossAccountLine>();
        foreach (var acct in accountMap.Values.OrderBy(a => a.Code, StringComparer.Ordinal).ThenBy(a => a.Id.ToString(), StringComparer.Ordinal))
        {
            balances.TryGetValue((acct.Id, propKey), out var raw);
            if (raw == 0m && !includeZero) continue;

            // Revenue: credit-normal means income earned = negative raw → display as positive.
            // Expense: debit-normal means cost incurred = positive raw → display as positive.
            var displayAmount = isRevenue ? -raw : raw;

            lines.Add(new ProfitAndLossAccountLine(
                AccountId: acct.Id,
                AccountCode: acct.Code,
                AccountName: acct.Name,
                Amount: displayAmount));
        }
        return lines;
    }
}
