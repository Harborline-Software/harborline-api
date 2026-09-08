using Microsoft.EntityFrameworkCore;

using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialPeriods.Services;
using Harborline.Api.LocalNodeHost.Data;

namespace Harborline.Api.LocalNodeHost.Data.Financial;

/// <summary>
/// EF Core–backed <see cref="IChartRepository"/> for the embedded local node
/// (T5 reports node-flip — the read-side reports surface served offline over the
/// now-node-resident GL). The reports cartridges (Trial Balance + Balance Sheet)
/// resolve the chart envelope via this repository to read
/// <see cref="ChartOfAccounts.RetainedEarningsAccountId"/> and the chart's base
/// currency / name.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reads the chart envelope from the node-resident store.</b> The
/// <c>charts_of_accounts</c> table is contributed by <c>FinancialLedgerEntityModule</c>
/// into <see cref="LocalNodeDbContext"/> (SQLite/SQLCipher, the SC-1 store-DEK-enveloped
/// <c>local-node.db</c>) and is the same source-of-truth the chart-of-accounts seed /
/// management routes write. Distinct from <see cref="NodeEfAccountResolver"/> (the
/// <c>IAccountResolver</c> account-by-id leaf surface) — this is the chart envelope, not
/// an account.
/// </para>
/// <para>
/// <b>Read-only (SC4-T9(b)).</b> A pure projection over <c>local-node.db</c> — no writes,
/// no kernel CRDT / per-team event-log access, no <c>IDomainEventPublisher</c>. It
/// references no <c>PostingEngine</c>/<c>FileBackedEventLog</c>/<c>IEventLog</c>, so the
/// SC4-T9(b) Layer-1 IL scan stays green and no recoverability sink is added.
/// </para>
/// <para>
/// <b>Singleton-safe.</b> Each call uses a short-lived context from the injected factory
/// (mirrors <see cref="NodeEfAccountResolver"/> / <see cref="NodeEfJournalStore"/>) — the
/// repository is registered as a singleton on the host and must not hold a scoped/disposed
/// context.
/// </para>
/// </remarks>
public sealed class NodeEfChartRepository : IChartRepository
{
    private readonly IDbContextFactory<LocalNodeDbContext> _contextFactory;

    /// <summary>Construct bound to the local-node EF context factory.</summary>
    public NodeEfChartRepository(IDbContextFactory<LocalNodeDbContext> contextFactory)
        => _contextFactory = contextFactory
            ?? throw new System.ArgumentNullException(nameof(contextFactory));

    /// <inheritdoc />
    public async Task<ChartOfAccounts?> GetAsync(
        ChartOfAccountsId chartId,
        CancellationToken cancellationToken = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        return await ctx.Set<ChartOfAccounts>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == chartId, cancellationToken)
            .ConfigureAwait(false);
    }
}
