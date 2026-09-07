using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.FinancialLedger.Services;

/// <summary>
/// Resolves a <see cref="LegalEntityId"/> to the <see cref="ChartOfAccountsId"/> its
/// books are kept on (ADR 0105 §3 / §4). The consolidation service holds a scope of
/// ENTITIES but the read substrate
/// (<see cref="IConsolidationReadModel.GetBalancesForChartsAsync"/>) fans out over
/// CHARTS, so this resolver bridges the two. One entity → one chart in v1 (a single
/// default chart per legal entity); the contract returns a single chart accordingly.
/// </summary>
/// <remarks>
/// <para>
/// <b>Fail-closed (mirrors invariant 0105-1's posture).</b> An entity with no
/// registered chart THROWS rather than resolving to a default or silently dropping the
/// entity from the rollup — a silently-omitted entity would under-count the group total,
/// the same failure mode the §2.2 chart∈tenant gate guards against. The throw names only
/// the caller-supplied tenant + entity ids; it never surfaces a foreign-tenant id.
/// </para>
/// <para>
/// <b>This is not the tenant boundary.</b> The load-bearing cross-tenant guarantee is
/// the §2.2 fail-closed chart∈tenant gate enforced by
/// <see cref="IConsolidationReadModel"/> at read time — a chart resolved here for one
/// tenant that does not actually belong to it is rejected downstream. This resolver is
/// keyed by tenant for symmetry with <see cref="IChartTenantResolver"/> and
/// <see cref="IChartCatalogService.GetDefaultChartIdAsync"/>, not as a security boundary.
/// </para>
/// </remarks>
public interface IEntityChartResolver
{
    /// <summary>
    /// Resolve the chart of accounts for <paramref name="entityId"/> within
    /// <paramref name="tenantId"/>. Throws when no chart is registered for the
    /// (tenant, entity) pair — fail-closed, never a silent default.
    /// </summary>
    /// <exception cref="InvalidOperationException">No chart is registered for the (tenant, entity).</exception>
    Task<ChartOfAccountsId> GetChartForEntityAsync(
        TenantId tenantId,
        LegalEntityId entityId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// In-memory <see cref="IEntityChartResolver"/> backed by a
/// <see cref="Dictionary{TKey,TValue}"/> keyed by (tenant, entity). Mirrors
/// <see cref="InMemoryChartTenantResolver"/>: an off-contract <see cref="Register"/>
/// seeding affordance for tests / Phase-1 wiring; production derives the mapping from
/// the entity → chart FK (<c>ChartOfAccounts.LegalEntityId</c>).
/// </summary>
public sealed class InMemoryEntityChartResolver : IEntityChartResolver
{
    private readonly Dictionary<(TenantId Tenant, LegalEntityId Entity), ChartOfAccountsId> _map = new();

    /// <summary>Seed or replace the chart for a (tenant, entity). Off-contract — not part of <see cref="IEntityChartResolver"/>.</summary>
    public void Register(TenantId tenantId, LegalEntityId entityId, ChartOfAccountsId chartId)
        => _map[(tenantId, entityId)] = chartId;

    /// <inheritdoc />
    public Task<ChartOfAccountsId> GetChartForEntityAsync(
        TenantId tenantId,
        LegalEntityId entityId,
        CancellationToken cancellationToken = default)
        => _map.TryGetValue((tenantId, entityId), out var chartId)
            ? Task.FromResult(chartId)
            : throw new InvalidOperationException(
                $"No chart of accounts is registered for entity '{entityId}' in tenant '{tenantId}'; " +
                "consolidation scope resolution failed closed (ADR 0105 §3/§4 — an unresolved entity is " +
                "never silently dropped from the rollup).");
}
