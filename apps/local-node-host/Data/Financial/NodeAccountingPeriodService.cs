using Microsoft.EntityFrameworkCore;

using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialPeriods.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.LocalNodeHost.Data;

namespace Harborline.Api.LocalNodeHost.Data.Financial;

/// <summary>
/// T1 local-first sweep — node-direct accounting-period WRITE + SEED service for the
/// embedded local node. Implements the Open → SoftClosed → Locked lifecycle (and the
/// fresh-install open-period seed) directly over <see cref="LocalNodeDbContext"/>, the
/// same node-resident SQLCipher store <see cref="NodeEfPeriodResolver"/> reads for the
/// posting Phase-4 period gate. This is the literal fix for "a fresh install can't open
/// a period offline" — without an open period covering today, posting (and therefore
/// issuing an invoice) is blocked by <c>NoPeriodForDate</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why node-direct, not the periods DI extension.</b> The existing
/// <c>blocks-financial-periods</c> <c>IPeriodCloseService</c> + <c>IFiscalPeriodRepository</c>
/// wire to an abstraction whose default DI registers an in-memory repository, not
/// <c>local-node.db</c>. Reusing them would require also wiring an EF-backed period +
/// fiscal-year repository set + the periods DI extension. The node-direct service here
/// queries <see cref="LocalNodeDbContext"/> straight over the SAME store the account/period
/// resolvers + journal store use — the cleanest long-term seam (the same rationale
/// <see cref="NodeEfPeriodResolver"/> documents for choosing node-direct over
/// <c>SqlitePeriodResolver</c>), and it keeps the node off the periods DI extension.
/// </para>
/// <para>
/// <b>Existing tables, NO new schema.</b> Uses the <c>fiscal_periods</c> + <c>fiscal_years</c>
/// tables already contributed by <c>FinancialLedgerEntityModule</c> into
/// <see cref="LocalNodeDbContext"/> (UPF F0 — a new period table would collide with that
/// mapping + trip the C2 both-provider parity test).
/// </para>
/// <para>
/// <b>Recoverability (SC-4-C2).</b> All writes target ONLY the recoverable, Store-DEK-enveloped
/// <c>local-node.db</c> via the EF context factory — no kernel CRDT / per-team event-log write,
/// no <c>IDomainEventPublisher</c> call. So the period-write path adds no orphan vector and
/// SC4-T9(b) stays green (this service references no <c>PostingEngine</c>/<c>FileBackedEventLog</c>/
/// <c>IEventLog</c>). Audit-envelope satisfied by durable-store presence (financial-cluster
/// durable-layer pattern — same deferred posture as the JE / COA node routes).
/// </para>
/// <para>
/// <b>Period model.</b> The fresh-install seed + the OPEN endpoint create MONTHLY periods. A
/// period requires a parent <see cref="FiscalYear"/>; the service resolves the covering open
/// year for the chart and creates one (a calendar year covering the date) if absent. This keeps
/// the single-device offline first-run self-sufficient — no Bridge year-setup step required.
/// </para>
/// <para>
/// <b>Singleton-safe.</b> Each call uses a short-lived context from the injected factory
/// (mirrors <see cref="NodeEfPeriodResolver"/> / <see cref="NodeEfAccountResolver"/>).
/// </para>
/// </remarks>
public sealed class NodeAccountingPeriodService
{
    private readonly IDbContextFactory<LocalNodeDbContext> _contextFactory;
    private readonly TimeProvider _time;

    /// <summary>Construct bound to the local-node EF context factory.</summary>
    public NodeAccountingPeriodService(IDbContextFactory<LocalNodeDbContext> contextFactory, TimeProvider time)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <summary>Outcome of a period write — success carries the resulting period.</summary>
    public enum Outcome
    {
        /// <summary>The write succeeded.</summary>
        Ok,
        /// <summary>No chart has been seeded yet — open a chart first.</summary>
        NoChart,
        /// <summary>The referenced period id was not found in the install chart.</summary>
        PeriodNotFound,
        /// <summary>The transition is illegal for the period's current status.</summary>
        InvalidTransition,
    }

    /// <summary>A period write result: an <see cref="Outcome"/> + the affected period on success.</summary>
    public sealed record PeriodResult(Outcome Outcome, FiscalPeriod? Period)
    {
        /// <summary><c>true</c> when the write succeeded.</summary>
        public bool IsSuccess => Outcome == Outcome.Ok;
    }

    /// <summary>
    /// Lists every fiscal period in the install chart, newest-start first. The <paramref name="chartId"/>
    /// out-parameter carries the resolved install chart id (<c>null</c> pre-seed).
    /// </summary>
    public async Task<(ChartOfAccountsId? ChartId, IReadOnlyList<FiscalPeriod> Periods)> ListAsync(
        CancellationToken ct = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var chartId = await ResolveChartIdAsync(ctx, ct).ConfigureAwait(false);
        if (chartId is null)
        {
            return (null, []);
        }

        var periods = await ctx.Set<FiscalPeriod>()
            .AsNoTracking()
            .Where(p => p.ChartId == chartId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var ordered = periods
            .OrderByDescending(p => p.StartDate)
            .ToList();
        return (chartId, ordered);
    }

    /// <summary>
    /// Ensures an OPEN period covers <paramref name="date"/> in the install chart, creating the
    /// covering fiscal year + monthly period if none exists, or REOPENing a SoftClosed covering
    /// period. A Locked covering period is an <see cref="Outcome.InvalidTransition"/> (unlock is a
    /// separate, deliberate admin step — not part of the offline first-run open path). Returns the
    /// open period covering the date on success. This is the offline "open a period" entry point.
    /// </summary>
    public async Task<PeriodResult> OpenForDateAsync(DateOnly date, CancellationToken ct = default)
    {
        var at = new Instant(_time.GetUtcNow());
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var chartId = await ResolveChartIdAsync(ctx, ct).ConfigureAwait(false);
        if (chartId is null)
        {
            return new PeriodResult(Outcome.NoChart, null);
        }

        // A covering period already exists?
        var covering = await ctx.Set<FiscalPeriod>()
            .FirstOrDefaultAsync(
                p => p.ChartId == chartId && p.StartDate <= date && p.EndDate >= date, ct)
            .ConfigureAwait(false);

        if (covering is not null)
        {
            switch (covering.Status)
            {
                case FiscalPeriodStatus.Open:
                    return new PeriodResult(Outcome.Ok, covering); // already open — idempotent
                case FiscalPeriodStatus.SoftClosed:
                    var reopened = covering with
                    {
                        Status = FiscalPeriodStatus.Open,
                        SoftClosedAtUtc = null,
                        LockedAtUtc = null,
                        Version = covering.Version + 1,
                    };
                    ctx.Entry(covering).CurrentValues.SetValues(reopened);
                    await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
                    return new PeriodResult(Outcome.Ok, reopened);
                default: // Locked — deliberate unlock required, not an auto-open
                    return new PeriodResult(Outcome.InvalidTransition, covering);
            }
        }

        // No covering period — resolve/create the fiscal year, then create a monthly Open period.
        var year = await ResolveOrCreateYearAsync(ctx, chartId.Value, date, at, ct).ConfigureAwait(false);

        var monthStart = new DateOnly(date.Year, date.Month, 1);
        var monthEnd = monthStart.AddMonths(1).AddDays(-1);
        var period = FiscalPeriod.CreateOpen(
            id:           FiscalPeriodId.NewId(),
            chartId:      chartId.Value,
            fiscalYearId: year.Id,
            kind:         FiscalPeriodKind.Monthly,
            label:        $"{monthStart:yyyy-MM}",
            startDate:    monthStart,
            endDate:      monthEnd,
            createdAtUtc: at);

        ctx.Set<FiscalPeriod>().Add(period);
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        return new PeriodResult(Outcome.Ok, period);
    }

    /// <summary>
    /// Closes a period: Open → SoftClosed by default, or Open/SoftClosed → Locked when
    /// <paramref name="lock"/> is <c>true</c>. An already-terminal transition (closing a SoftClosed
    /// without lock, or locking a Locked) is an <see cref="Outcome.InvalidTransition"/>.
    /// </summary>
    public async Task<PeriodResult> CloseAsync(string periodId, bool @lock, CancellationToken ct = default)
    {
        var now = new Instant(_time.GetUtcNow());
        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var chartId = await ResolveChartIdAsync(ctx, ct).ConfigureAwait(false);
        if (chartId is null)
        {
            return new PeriodResult(Outcome.NoChart, null);
        }

        var period = await ctx.Set<FiscalPeriod>()
            .FirstOrDefaultAsync(p => p.Id == new FiscalPeriodId(periodId), ct)
            .ConfigureAwait(false);
        if (period is null || period.ChartId != chartId)
        {
            return new PeriodResult(Outcome.PeriodNotFound, null);
        }

        FiscalPeriod next;
        if (@lock)
        {
            if (period.Status == FiscalPeriodStatus.Locked)
            {
                return new PeriodResult(Outcome.InvalidTransition, period);
            }
            // Open → Locked auto-soft-closes inline (mirrors IPeriodCloseService.LockAsync).
            next = period with
            {
                Status = FiscalPeriodStatus.Locked,
                SoftClosedAtUtc = period.SoftClosedAtUtc ?? now,
                LockedAtUtc = now,
                Version = period.Version + 1,
            };
        }
        else
        {
            if (period.Status != FiscalPeriodStatus.Open)
            {
                return new PeriodResult(Outcome.InvalidTransition, period);
            }
            next = period with
            {
                Status = FiscalPeriodStatus.SoftClosed,
                SoftClosedAtUtc = now,
                Version = period.Version + 1,
            };
        }

        ctx.Entry(period).CurrentValues.SetValues(next);
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        return new PeriodResult(Outcome.Ok, next);
    }

    /// <summary>
    /// Fresh-install open-period seed: ensures the install chart has an OPEN period covering
    /// <paramref name="today"/>, so a brand-new offline install can immediately issue an invoice
    /// without a <c>NoPeriodForDate</c> block. Idempotent (a no-op when a covering open period
    /// already exists). Returns <c>false</c> when no chart is seeded yet (the caller seeds the
    /// chart first). Invoked from the chart-seed path on the node.
    /// </summary>
    public async Task<bool> SeedOpenPeriodForTodayAsync(DateOnly today, CancellationToken ct = default)
    {
        var result = await OpenForDateAsync(today, ct).ConfigureAwait(false);
        return result.Outcome != Outcome.NoChart;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────

    private static async Task<ChartOfAccountsId?> ResolveChartIdAsync(
        LocalNodeDbContext ctx,
        CancellationToken ct)
    {
        // SQLite cannot ORDER BY a DateTimeOffset-converted Instant server-side — materialise
        // then order in memory (single-device node has at most a handful of charts). Mirrors
        // ChartOfAccountsManagementRoutes.ResolveChartIdAsync (bug logged separately).
        var charts = await ctx.Set<ChartOfAccounts>()
            .AsNoTracking()
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return charts
            .OrderByDescending(c => c.CreatedAtUtc.Value)
            .FirstOrDefault()
            ?.Id;
    }

    private static async Task<FiscalYear> ResolveOrCreateYearAsync(
        LocalNodeDbContext ctx,
        ChartOfAccountsId chartId,
        DateOnly date,
        Instant at,
        CancellationToken ct)
    {
        var existing = await ctx.Set<FiscalYear>()
            .FirstOrDefaultAsync(
                y => y.ChartId == chartId && y.StartDate <= date && y.EndDate >= date, ct)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        // Create a calendar fiscal year covering the date (single-device default; a custom
        // fiscal-year-start would re-create this at onboarding — out of scope for the offline
        // first-run open path).
        var yearStart = new DateOnly(date.Year, 1, 1);
        var yearEnd = new DateOnly(date.Year, 12, 31);
        var year = FiscalYear.CreateOpen(
            id:        FiscalYearId.NewId(),
            chartId:   chartId,
            label:     $"{date.Year}",
            startDate: yearStart,
            endDate:   yearEnd,
            createdAtUtc: at);

        ctx.Set<FiscalYear>().Add(year);
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        return year;
    }
}
