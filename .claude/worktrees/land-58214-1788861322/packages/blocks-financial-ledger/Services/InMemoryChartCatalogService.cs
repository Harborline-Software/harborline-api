using System.Collections.Concurrent;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.FinancialLedger.Services;

/// <summary>
/// In-memory <see cref="IChartCatalogService"/> backed by a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/>. v1 implementation
/// suitable for the desktop / kitchen-sink / signal-bridge in-memory
/// posture. A SQLite-backed implementation lands when the financial
/// persistence hand-off promotes the in-memory v1 to a real store.
/// </summary>
public sealed class InMemoryChartCatalogService : IChartCatalogService
{
    private readonly ConcurrentDictionary<TenantId, ChartOfAccountsId> _defaults = new();

    /// <inheritdoc />
    public Task<ChartOfAccountsId?> GetDefaultChartIdAsync(TenantId tenantId, CancellationToken cancellationToken = default)
    {
        if (tenantId == default) return Task.FromResult<ChartOfAccountsId?>(null);
        return Task.FromResult(_defaults.TryGetValue(tenantId, out var chartId)
            ? chartId
            : (ChartOfAccountsId?)null);
    }

    /// <inheritdoc />
    public Task RegisterDefaultChartAsync(TenantId tenantId, ChartOfAccountsId chartId, CancellationToken cancellationToken = default)
    {
        if (tenantId == default) throw new ArgumentException("TenantId is required.", nameof(tenantId));
        if (chartId == default) throw new ArgumentException("ChartId is required.", nameof(chartId));
        _defaults[tenantId] = chartId;
        return Task.CompletedTask;
    }
}
