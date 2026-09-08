using Microsoft.EntityFrameworkCore;

using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialPeriods.Models;
using Harborline.Api.Blocks.FinancialPeriods.Services;
using Harborline.Api.LocalNodeHost.Data;

namespace Harborline.Api.LocalNodeHost.Data.Banking;

/// <summary>
/// EF Core–backed <see cref="IFiscalPeriodRepository"/> for the embedded local node
/// (T3 local-first sweep — the banking node-flip).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> The node period substrate (T1) wired a
/// <see cref="Financial.NodeEfPeriodResolver"/> (<c>IPeriodResolver</c>) + a
/// <c>NodeAccountingPeriodService</c>, but NOT the <see cref="IFiscalPeriodRepository"/> abstraction
/// — neither is that interface. <see cref="Harborline.Api.Blocks.Banking.Matching.AcceptMatchService"/>
/// consumes <see cref="IFiscalPeriodRepository"/> for its fiscal-period Locked gate, and the banking
/// accept endpoint resolves the covering period for a statement line via
/// <see cref="FindByChartAndDateAsync"/>. This repo gives the banking composition a node-resident
/// EF-backed <see cref="IFiscalPeriodRepository"/> over the SAME recoverable <c>local-node.db</c> the
/// resolver reads — keeping the node off the periods DI extension (whose default registers an
/// in-memory repo). It mirrors the read pattern of <see cref="Financial.NodeEfPeriodResolver"/>.
/// </para>
/// <para>
/// <b>FiscalPeriod keying.</b> <see cref="FiscalPeriod"/> is keyed on <c>Id</c> with no
/// <c>TenantId</c> column (install-global on the single-device node), so reads are by id / chart+date
/// / fiscal-year only — no tenant scoping (mirrors <see cref="Financial.NodeEfPeriodResolver"/>).
/// <see cref="FindByChartAndDateAsync"/> uses the same inclusive
/// <c>ChartId == chartId AND StartDate &lt;= date &lt;= EndDate</c> covering-period semantics.
/// </para>
/// <para>
/// <b>Recoverability (SC-4-C2 condition (d)).</b> Reads + the InsertAsync/UpdateAsync writes touch
/// ONLY the recoverable, Store-DEK-enveloped <c>local-node.db</c> — no kernel CRDT / per-team
/// event-log write. In the banking flow the period repo is consulted read-only (the period
/// open/close WRITE path stays in T1's <c>NodeAccountingPeriodService</c>); the write members are
/// implemented for interface completeness.
/// </para>
/// <para>
/// <b>Singleton-safe</b> — short-lived context per call from the injected factory.
/// </para>
/// </remarks>
public sealed class NodeEfFiscalPeriodRepository : IFiscalPeriodRepository
{
    private readonly IDbContextFactory<LocalNodeDbContext> _contextFactory;

    /// <summary>Construct bound to the local-node EF context factory.</summary>
    public NodeEfFiscalPeriodRepository(IDbContextFactory<LocalNodeDbContext> contextFactory)
        => _contextFactory = contextFactory
            ?? throw new ArgumentNullException(nameof(contextFactory));

    /// <inheritdoc />
    public async Task<FiscalPeriod?> GetAsync(FiscalPeriodId id, CancellationToken cancellationToken = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await ctx.Set<FiscalPeriod>()
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FiscalPeriod>> GetByFiscalYearAsync(
        FiscalYearId fiscalYearId, CancellationToken cancellationToken = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var periods = await ctx.Set<FiscalPeriod>()
            .AsNoTracking()
            .Where(p => p.FiscalYearId == fiscalYearId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        // StartDate is DateOnly (translatable), but order in memory to stay uniform with the node's
        // materialise-then-order posture; the single-device period count is tiny.
        return periods.OrderBy(p => p.StartDate).ToList();
    }

    /// <inheritdoc />
    public async Task<FiscalPeriod?> FindByChartAndDateAsync(
        ChartOfAccountsId chartId, DateOnly date, CancellationToken cancellationToken = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await ctx.Set<FiscalPeriod>()
            .AsNoTracking()
            .Where(p => p.ChartId == chartId && p.StartDate <= date && p.EndDate >= date)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task InsertAsync(FiscalPeriod period, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(period);

        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        ctx.Set<FiscalPeriod>().Add(period);
        await ctx.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> UpdateAsync(FiscalPeriod period, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(period);

        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var exists = await ctx.Set<FiscalPeriod>()
            .AsNoTracking()
            .AnyAsync(p => p.Id == period.Id, cancellationToken)
            .ConfigureAwait(false);
        if (!exists)
            return false;

        ctx.Set<FiscalPeriod>().Update(period);
        await ctx.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <see cref="FiscalPeriod"/> carries NO external-ref column (the in-memory repo tracks it in a
    /// side map populated by the migration importer, which does not run on the node). The banking
    /// accept flow never consults this member; it is implemented as a no-op returning null so the
    /// interface is complete without inventing a column that does not exist in the node schema.
    /// </remarks>
    public Task<FiscalPeriod?> GetByExternalRefAsync(string externalRef, CancellationToken cancellationToken = default)
        => Task.FromResult<FiscalPeriod?>(null);
}
