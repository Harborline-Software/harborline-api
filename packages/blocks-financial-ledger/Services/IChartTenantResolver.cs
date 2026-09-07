using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.FinancialLedger.Services;

/// <summary>
/// Resolves chart-of-accounts → owning-tenant ownership for the
/// fail-closed chart∈tenant gate on the cross-entity consolidation read
/// (ADR 0105 §2.2, invariant 0105-1).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a distinct seam.</b> The consolidation fan-out
/// (<see cref="IConsolidationReadModel.GetBalancesForChartsAsync"/>) is the
/// textbook location for a missing tenant predicate: it accepts a
/// caller-supplied LIST of chart ids and must reject any chart not owned by
/// the passed tenant. That ownership question is a single responsibility —
/// "does this chart belong to this tenant" — kept separate from the
/// default-chart catalog (<see cref="IChartCatalogService"/>, whose contract
/// cohort-2 Bridge handlers depend on) and from chart storage / seeding.
/// </para>
/// <para>
/// <b>Ownership chain.</b> The authoritative ownership is
/// <see cref="ChartOfAccounts.LegalEntityId"/> → <c>LegalEntity.TenantId</c>
/// (ADR 0104: one chart == one <c>LegalEntity</c>; the entity carries the
/// tenant). A production (SQLite-backed) implementation resolves the chart
/// through that chain. The in-memory v1
/// (<see cref="InMemoryChartTenantResolver"/>) holds explicit registrations,
/// matching the established in-memory-substrate posture of the cluster
/// (a SQLite-backed implementation lands when the financial persistence
/// hand-off promotes the in-memory v1).
/// </para>
/// </remarks>
public interface IChartTenantResolver
{
    /// <summary>
    /// Returns <c>true</c> iff <paramref name="chartId"/> is owned by a legal
    /// entity belonging to <paramref name="tenantId"/>. A chart that does not
    /// resolve to any entity in the tenant — including an entirely unknown
    /// chart — returns <c>false</c>, so the consolidation gate fails closed
    /// (rejects) rather than silently dropping the chart.
    /// </summary>
    Task<bool> IsChartInTenantAsync(TenantId tenantId, ChartOfAccountsId chartId, CancellationToken cancellationToken = default);
}
