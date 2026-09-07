using Microsoft.EntityFrameworkCore;

using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialLedger.Services;
using Harborline.Api.LocalNodeHost.Data;

namespace Harborline.Api.LocalNodeHost.Data.Financial;

/// <summary>
/// EF Core–backed <see cref="IAccountResolver"/> for the embedded local node
/// (Cohort D Step 2a — the node-side financial posting foundation; ADR 0113
/// ABSOLUTE local-first).
/// </summary>
/// <remarks>
/// <para>
/// <b>Resolves chart-of-accounts accounts from the node-resident store.</b> The
/// <c>gl_accounts</c> table is contributed by <c>FinancialLedgerEntityModule</c> into
/// <see cref="LocalNodeDbContext"/> (SQLite/SQLCipher, the SC-1 store-DEK-enveloped
/// <c>local-node.db</c>) and is the same source-of-truth the chart-of-accounts seed routes
/// write (<c>HostedChartOfAccountsApiEndpoint</c>). This resolver gives
/// <see cref="JournalPostingService"/> a node-resident account-validity backend so its
/// Phase-3 (account validity) gate is real on a single-device install — without depending
/// on signal-bridge.
/// </para>
/// <para>
/// <b>Recoverability (SC-4-C2 condition (d)).</b> This resolver READS ONLY <c>local-node.db</c>
/// (Store-DEK-enveloped, recoverable) — it performs no kernel CRDT / per-team event-log write,
/// no <c>IDomainEventPublisher</c> call. It is a pure read seam. The whole posting path on the
/// node therefore writes only recoverable value (per the security-engineering SC4-C2 verdict
/// 2026-06-15, Q1).
/// </para>
/// <para>
/// <b>No tenant column on <c>GLAccount</c>.</b> The chart-of-accounts is install-global on the
/// single-device node (the EF model keys <c>gl_accounts</c> on <c>Id</c> with no <c>TenantId</c>
/// property; see <c>FinancialLedgerEntityModule.ConfigureChartOfAccounts</c>). So the
/// <c>tenantId</c> passed to the node posting composition pins the JOURNAL ENTRY tenant
/// (the active-team-derived tenant — <c>ActiveTeamTenantContext</c> / <c>NodeTenant.Resolve</c>,
/// ADR 0032 identity layer — not a fixed <c>"local"</c>); account lookup is by id only, mirroring the
/// Bridge resolver shape.
/// </para>
/// <para>
/// <b>Singleton-safe.</b> Each call uses a short-lived context from the injected factory (mirrors
/// <see cref="NodeEfJournalStore"/>) — the resolver is registered as a singleton on the host and
/// must not hold a scoped/disposed context.
/// </para>
/// </remarks>
public sealed class NodeEfAccountResolver : IAccountResolver
{
    private readonly IDbContextFactory<LocalNodeDbContext> _contextFactory;

    /// <summary>Construct bound to the local-node EF context factory.</summary>
    public NodeEfAccountResolver(IDbContextFactory<LocalNodeDbContext> contextFactory)
        => _contextFactory = contextFactory
            ?? throw new System.ArgumentNullException(nameof(contextFactory));

    /// <inheritdoc />
    public async Task<GLAccount?> GetAsync(GLAccountId id, CancellationToken cancellationToken = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        return await ctx.Set<GLAccount>()
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GLAccount>> EnumerateForChartAsync(
        ChartOfAccountsId chartId,
        bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var query = ctx.Set<GLAccount>()
            .AsNoTracking()
            .Where(a => a.ChartId == chartId);
        if (!includeInactive)
        {
            query = query.Where(a => a.IsActive);
        }
        return await query.ToListAsync(cancellationToken).ConfigureAwait(false);
    }
}
