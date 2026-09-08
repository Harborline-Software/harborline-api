using System.Collections.Concurrent;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.FinancialLedger.Services;

/// <summary>
/// In-memory <see cref="IChartTenantResolver"/> backed by a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> of chart → owning-tenant
/// registrations. v1 implementation suitable for the desktop / kitchen-sink /
/// signal-bridge in-memory posture. A SQLite-backed implementation that
/// resolves the authoritative chart → <c>LegalEntity</c> → tenant chain lands
/// when the financial persistence hand-off promotes the in-memory v1.
/// </summary>
/// <remarks>
/// Registration (<see cref="Register"/>) is an in-memory-v1 seeding affordance
/// only — it is NOT on the <see cref="IChartTenantResolver"/> contract, because
/// a production implementation derives ownership from the chart → entity → tenant
/// FK chain rather than an explicit registry. Hosts / tests that use this v1
/// seed the mapping via <see cref="Register"/> (mirrors
/// <see cref="InMemoryChartCatalogService.RegisterDefaultChartAsync"/>).
/// </remarks>
public sealed class InMemoryChartTenantResolver : IChartTenantResolver
{
    private readonly ConcurrentDictionary<ChartOfAccountsId, TenantId> _ownership = new();

    /// <summary>
    /// Seed the in-memory mapping: record that <paramref name="chartId"/> is
    /// owned by <paramref name="tenantId"/>. Last-write-wins. Rejects a default
    /// tenant or chart id.
    /// </summary>
    public void Register(TenantId tenantId, ChartOfAccountsId chartId)
    {
        if (tenantId == default) throw new ArgumentException("TenantId is required.", nameof(tenantId));
        if (chartId == default) throw new ArgumentException("ChartId is required.", nameof(chartId));
        _ownership[chartId] = tenantId;
    }

    /// <inheritdoc />
    public Task<bool> IsChartInTenantAsync(TenantId tenantId, ChartOfAccountsId chartId, CancellationToken cancellationToken = default)
    {
        if (tenantId == default) throw new ArgumentException("TenantId is required.", nameof(tenantId));
        var inTenant = _ownership.TryGetValue(chartId, out var owner) && owner == tenantId;
        return Task.FromResult(inTenant);
    }
}
