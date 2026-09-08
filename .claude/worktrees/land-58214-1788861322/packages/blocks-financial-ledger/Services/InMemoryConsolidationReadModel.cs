using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.FinancialLedger.Services;

/// <summary>
/// In-memory <see cref="IConsolidationReadModel"/>. Its v1 implementation
/// literally calls the shipped
/// <see cref="IGeneralLedgerReadModel.GetAccountBalancesAsOfAsync"/> once per
/// chart and assembles the per-chart dictionary — no kernel change, no caller
/// migration, no adapter (ADR 0105 §2.1). The fail-closed chart∈tenant gate
/// (§2.2) is enforced via <see cref="IChartTenantResolver"/> before any read.
/// </summary>
public sealed class InMemoryConsolidationReadModel : IConsolidationReadModel
{
    private readonly IGeneralLedgerReadModel _readModel;
    private readonly IChartTenantResolver _chartTenantResolver;

    /// <summary>Construct bound to the single-chart read model and the chart→tenant resolver.</summary>
    public InMemoryConsolidationReadModel(IGeneralLedgerReadModel readModel, IChartTenantResolver chartTenantResolver)
    {
        _readModel = readModel ?? throw new ArgumentNullException(nameof(readModel));
        _chartTenantResolver = chartTenantResolver ?? throw new ArgumentNullException(nameof(chartTenantResolver));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<ChartOfAccountsId, IReadOnlyDictionary<GLAccountId, decimal>>>
        GetBalancesForChartsAsync(
            TenantId tenantId,
            IReadOnlyList<ChartOfAccountsId> charts,
            System.DateOnly asOf,
            string snapshotMarker,
            CancellationToken cancellationToken = default)
    {
        var distinctCharts = await ValidateChartsInTenantAsync(tenantId, charts, cancellationToken).ConfigureAwait(false);

        var result = new Dictionary<ChartOfAccountsId, IReadOnlyDictionary<GLAccountId, decimal>>();
        foreach (var chartId in distinctCharts)
        {
            result[chartId] = await _readModel
                .GetAccountBalancesAsOfAsync(tenantId, chartId, asOf, snapshotMarker, cancellationToken)
                .ConfigureAwait(false);
        }
        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<ChartOfAccountsId, IReadOnlyDictionary<GLAccountId, IReadOnlyDictionary<LegalEntityId, decimal>>>>
        GetIntercompanyBalancesForChartsAsync(
            TenantId tenantId,
            IReadOnlyList<ChartOfAccountsId> charts,
            System.DateOnly asOf,
            string snapshotMarker,
            CancellationToken cancellationToken = default)
    {
        // SAME fail-closed gate as GetBalancesForChartsAsync — not forked: both
        // fan-outs run the identical validation helper before any read (ADR 0105
        // §2.2, invariant 0105-1).
        var distinctCharts = await ValidateChartsInTenantAsync(tenantId, charts, cancellationToken).ConfigureAwait(false);

        var result = new Dictionary<ChartOfAccountsId, IReadOnlyDictionary<GLAccountId, IReadOnlyDictionary<LegalEntityId, decimal>>>();
        foreach (var chartId in distinctCharts)
        {
            result[chartId] = await _readModel
                .GetIntercompanyBalancesAsOfAsync(tenantId, chartId, asOf, snapshotMarker, cancellationToken)
                .ConfigureAwait(false);
        }
        return result;
    }

    /// <summary>
    /// The single fail-closed chart∈tenant gate (ADR 0105 §2.2, invariant 0105-1)
    /// that BOTH fan-outs call. Dedupes the chart list (a chart named more than once
    /// is read once; value-equality dedupe preserves first-seen order), then validates
    /// EVERY requested chart belongs to <paramref name="tenantId"/> BEFORE any read.
    /// A foreign / unknown chart REJECTS the whole request (throw) — never a
    /// silent-empty or a partial result, which would under-count the group total.
    /// The throw names only the caller-supplied tenant + chart ids; it never surfaces
    /// a foreign-tenant id.
    /// </summary>
    private async Task<List<ChartOfAccountsId>> ValidateChartsInTenantAsync(
        TenantId tenantId,
        IReadOnlyList<ChartOfAccountsId> charts,
        CancellationToken cancellationToken)
    {
        if (tenantId == default) throw new ArgumentException("TenantId is required.", nameof(tenantId));
        ArgumentNullException.ThrowIfNull(charts);

        var distinctCharts = charts.Distinct().ToList();
        foreach (var chartId in distinctCharts)
        {
            if (!await _chartTenantResolver.IsChartInTenantAsync(tenantId, chartId, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    $"Chart '{chartId}' is not owned by tenant '{tenantId}'; cross-tenant consolidation read rejected " +
                    "(ADR 0105 §2.2 fail-closed chart-in-tenant invariant 0105-1).");
            }
        }
        return distinctCharts;
    }
}
