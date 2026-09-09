using Microsoft.EntityFrameworkCore;

using Harborline.Api.Blocks.FinancialAr.Models;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.LocalNodeHost.Data;

namespace Harborline.Api.LocalNodeHost.Data.Financial;

/// <summary>
/// T1 local-first sweep — node-computed accounting-summary aggregation for the embedded
/// local node. Replaces the Bridge ERPNext proxy
/// (<c>/api/v1/erpnext/accounting/{summary,outstanding}</c>) so the Accounting dashboard
/// renders fully offline from the now-node-resident GL + AR data.
/// </summary>
/// <remarks>
/// <para>
/// <b>Summary</b> = current-calendar-month income / expenses / net, computed from POSTED
/// journal entries in the install chart, joined to <c>gl_accounts</c> by account TYPE:
/// income = (credits − debits) over Revenue accounts; expenses = (debits − credits) over
/// Expense accounts; net = income − expenses. Posted-only (Draft/Reversed excluded), mirroring
/// <c>IGeneralLedgerReadModel</c>'s posted-only discipline.
/// </para>
/// <para>
/// <b>Outstanding</b> = open invoices (Issued / PartiallyPaid) with a positive balance, projected
/// from the node-resident AR store (the same <see cref="NodeEfInvoiceRepository"/> the node invoice
/// routes serve), newest-due first.
/// </para>
/// <para>
/// <b>Read-only.</b> Pure projection over <c>local-node.db</c> — no writes, no kernel CRDT /
/// event-log access, no <c>IDomainEventPublisher</c>. SC4-T9(b) unaffected (references no
/// <c>PostingEngine</c>/<c>FileBackedEventLog</c>/<c>IEventLog</c>).
/// </para>
/// <para>
/// <b>Singleton-safe.</b> Each call uses a short-lived context from the injected factory.
/// </para>
/// </remarks>
public sealed class NodeAccountingSummaryService
{
    private readonly IDbContextFactory<LocalNodeDbContext> _contextFactory;
    private readonly Harborline.Api.Kernel.Runtime.Teams.IActiveTeamAccessor _activeTeam;

    /// <summary>Construct bound to the local-node EF context factory + the active-team accessor.</summary>
    public NodeAccountingSummaryService(
        IDbContextFactory<LocalNodeDbContext> contextFactory,
        Harborline.Api.Kernel.Runtime.Teams.IActiveTeamAccessor activeTeam)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _activeTeam = activeTeam ?? throw new ArgumentNullException(nameof(activeTeam));
    }

    /// <summary>The active org's data tenant (ADR 0032 identity layer) — replaces the retired "local" literal.</summary>
    private TenantId LocalTenantId => NodeTenant.Resolve(_activeTeam);

    /// <summary>Current-period income/expense summary (period label + income + expenses + net).</summary>
    public sealed record Summary(string Period, decimal Income, decimal Expenses, decimal Net);

    /// <summary>One open (outstanding) invoice in the dashboard projection.</summary>
    public sealed record OutstandingInvoice(
        string Name, string Customer, decimal OutstandingAmount, string DueDate, string Status);

    /// <summary>
    /// Computes the current-calendar-month income / expenses / net from posted journal entries in
    /// the install chart. Returns a zeroed summary (income/expenses/net = 0) when no chart is
    /// seeded or no posted activity exists this month — so the dashboard renders cleanly pre-data.
    /// </summary>
    public async Task<Summary> GetSummaryAsync(DateOnly? today = null, CancellationToken ct = default)
    {
        var now = today ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var monthStart = new DateOnly(now.Year, now.Month, 1);
        var monthEnd = monthStart.AddMonths(1).AddDays(-1);
        var periodLabel = $"{monthStart:yyyy-MM}";

        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var chartId = await ResolveChartIdAsync(ctx, ct).ConfigureAwait(false);
        if (chartId is null)
        {
            return new Summary(periodLabel, 0m, 0m, 0m);
        }

        // account id → type map for the chart (so JE lines can be classified Revenue vs Expense).
        var accountTypes = await ctx.Set<GLAccount>()
            .AsNoTracking()
            .Where(a => a.ChartId == chartId)
            .Select(a => new { a.Id, a.Type })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var typeById = accountTypes.ToDictionary(a => a.Id, a => a.Type);

        // Posted JEs in the chart whose EntryDate falls in the current month. Lines are a
        // JSONB-converted column, so they round-trip with the entry; the date window is filtered
        // server-side, the per-line classification is in memory (single-device set is small).
        var entries = await ctx.Set<JournalEntry>()
            .AsNoTracking()
            .Where(e => e.ChartId == chartId
                && e.Status == JournalEntryStatus.Posted
                && e.EntryDate >= monthStart && e.EntryDate <= monthEnd)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        decimal income = 0m;
        decimal expenses = 0m;
        foreach (var entry in entries)
        {
            foreach (var line in entry.Lines)
            {
                if (!typeById.TryGetValue(line.AccountId, out var type))
                {
                    continue; // line on an account outside this chart's catalog — skip
                }
                switch (type)
                {
                    case GLAccountType.Revenue:
                        // Revenue is credit-normal → income increases on credits.
                        income += line.Credit - line.Debit;
                        break;
                    case GLAccountType.Expense:
                        // Expense is debit-normal → expense increases on debits.
                        expenses += line.Debit - line.Credit;
                        break;
                    default:
                        break; // Asset/Liability/Equity do not contribute to the P&L summary
                }
            }
        }

        return new Summary(periodLabel, income, expenses, income - expenses);
    }

    /// <summary>
    /// Lists the open (outstanding) invoices in the install chart — Issued / PartiallyPaid with a
    /// positive balance — newest-due first, projected to the dashboard wire shape. Empty when no
    /// chart is seeded or nothing is open.
    /// </summary>
    public async Task<IReadOnlyList<OutstandingInvoice>> GetOutstandingAsync(CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var chartId = await ResolveChartIdAsync(ctx, ct).ConfigureAwait(false);
        if (chartId is null)
        {
            return [];
        }

        var invoices = await ctx.Set<Invoice>()
            .AsNoTracking()
            .Where(i => i.TenantId == LocalTenantId && i.ChartId == chartId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return invoices
            .Where(i => i.Status.IsOpen() && i.Balance > 0m)
            .OrderByDescending(i => i.DueDate)
            .Select(i => new OutstandingInvoice(
                Name:              i.InvoiceNumber,
                Customer:          i.CustomerId.Value,
                OutstandingAmount: i.Balance,
                DueDate:           i.DueDate.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                Status:            i.Status.ToString()))
            .ToList();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────

    private static async Task<ChartOfAccountsId?> ResolveChartIdAsync(
        LocalNodeDbContext ctx,
        CancellationToken ct)
    {
        // SQLite cannot ORDER BY a DateTimeOffset-converted Instant server-side — materialise then
        // order in memory (single-device node has at most a handful of charts). Mirrors the COA /
        // periods chart resolution.
        var charts = await ctx.Set<ChartOfAccounts>()
            .AsNoTracking()
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return charts
            .OrderByDescending(c => c.CreatedAtUtc.Value)
            .FirstOrDefault()
            ?.Id;
    }
}
