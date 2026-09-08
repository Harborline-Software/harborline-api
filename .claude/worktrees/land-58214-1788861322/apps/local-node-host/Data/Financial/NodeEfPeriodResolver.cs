using Microsoft.EntityFrameworkCore;

using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialLedger.Services;
using Harborline.Api.Blocks.FinancialPeriods.Models;
using Harborline.Api.LocalNodeHost.Data;

namespace Harborline.Api.LocalNodeHost.Data.Financial;

/// <summary>
/// EF Core–backed <see cref="IPeriodResolver"/> for the embedded local node
/// (Cohort D Step 2a — the node-side financial posting foundation; ADR 0113
/// ABSOLUTE local-first).
/// </summary>
/// <remarks>
/// <para>
/// <b>Resolves the fiscal period covering a chart + date from the node-resident store</b> and
/// projects it to the ledger's minimal <see cref="IPeriodResolver.PeriodSnapshot"/> shape. The
/// <c>fiscal_periods</c> table is contributed by <c>FinancialLedgerEntityModule</c> into
/// <see cref="LocalNodeDbContext"/>; this resolver gives <see cref="JournalPostingService"/> a
/// node-resident period-gating backend so its Phase-4 (period gating) gate is real on a
/// single-device install.
/// </para>
/// <para>
/// <b>Why not reuse <c>SqlitePeriodResolver</c>?</b> The existing
/// <c>blocks-financial-periods.SqlitePeriodResolver</c> wires to the
/// <c>IFiscalPeriodRepository</c> abstraction, NOT to <see cref="LocalNodeDbContext"/>. Reusing it
/// would require also wiring an EF-backed <c>IFiscalPeriodRepository</c> + the periods DI extension
/// (whose default registers an in-memory repository, not <c>local-node.db</c>). The node-direct
/// resolver here queries <see cref="LocalNodeDbContext"/> straight over the SAME SQLCipher store the
/// account resolver + journal store use — the cleanest long-term seam, and it keeps the node off the
/// periods DI extension entirely.
/// </para>
/// <para>
/// <b>Recoverability (SC-4-C2 condition (d)).</b> Pure READ over the recoverable, Store-DEK-enveloped
/// <c>local-node.db</c> — no kernel CRDT / per-team event-log write, no <c>IDomainEventPublisher</c>
/// call. (security-engineering SC4-C2 verdict 2026-06-15, Q1.)
/// </para>
/// <para>
/// <b>FindByChartAndDate semantics</b> mirror <c>InMemoryFiscalPeriodRepository</c>: the covering
/// period satisfies <c>ChartId == chartId AND StartDate &lt;= date &lt;= EndDate</c> (inclusive). The
/// <c>ix_fiscal_periods_chart_dates</c> index backs this access pattern. <c>FiscalPeriod</c> is keyed
/// on <c>Id</c> with no <c>TenantId</c> column (install-global on the single-device node), so the
/// query is by chart + date only.
/// </para>
/// <para>
/// <b>Singleton-safe.</b> Each call uses a short-lived context from the injected factory.
/// </para>
/// </remarks>
public sealed class NodeEfPeriodResolver : IPeriodResolver
{
    private readonly IDbContextFactory<LocalNodeDbContext> _contextFactory;

    /// <summary>Construct bound to the local-node EF context factory.</summary>
    public NodeEfPeriodResolver(IDbContextFactory<LocalNodeDbContext> contextFactory)
        => _contextFactory = contextFactory
            ?? throw new System.ArgumentNullException(nameof(contextFactory));

    /// <inheritdoc />
    public async Task<IPeriodResolver.PeriodSnapshot?> ResolveAsync(
        ChartOfAccountsId chartId,
        DateOnly date,
        CancellationToken cancellationToken = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        var period = await ctx.Set<FiscalPeriod>()
            .AsNoTracking()
            .Where(p => p.ChartId == chartId && p.StartDate <= date && p.EndDate >= date)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (period is null)
        {
            return null;
        }

        return new IPeriodResolver.PeriodSnapshot(
            PeriodId: period.Id.Value,
            ChartId:  period.ChartId.Value,
            Status:   period.Status switch
            {
                FiscalPeriodStatus.Open       => IPeriodResolver.Status.Open,
                FiscalPeriodStatus.SoftClosed => IPeriodResolver.Status.SoftClosed,
                FiscalPeriodStatus.Locked     => IPeriodResolver.Status.Locked,
                // Fail-closed on an unknown enum case (mirrors SqlitePeriodResolver) —
                // silently reporting Open would open the posting-gate for a status the
                // ledger doesn't know how to handle.
                _ => throw new System.InvalidOperationException(
                    $"Unhandled FiscalPeriodStatus '{period.Status}' for period {period.Id.Value}; "
                    + "NodeEfPeriodResolver.Status switch must be extended to cover the new case."),
            });
    }
}
