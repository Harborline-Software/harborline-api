using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Harborline.Api.Blocks.FinancialAp.Models;
using Harborline.Api.Blocks.FinancialAp.Services;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Blocks.People.Foundation.Services;
using Harborline.Api.Blocks.Reports.Exceptions;

namespace Harborline.Api.Blocks.Reports.Cartridges.ApAgingSummary;

/// <summary>
/// AP Aging Summary cartridge — mirrors <see cref="Cartridges.ArAgingSummary.ArAgingSummaryCartridge"/>
/// with payable (vendor/bill) semantics instead of receivable (customer/invoice) semantics.
/// Delegates aging computation to <see cref="IApAgingService"/> (which owns
/// the bucket logic), then builds per-vendor and per-property rollups
/// at the portfolio level.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read-side discipline.</b> No writes, no event publication.
/// The cartridge is a pure orchestrator over read-side services.
/// </para>
/// <para>
/// <b>Tenant isolation.</b> Per the hand-off D4-C precedent established
/// in Trial Balance: the caller is responsible for resolving
/// <c>ChartId → LegalEntity → TenantId</c> before calling the
/// cartridge. The cartridge validates chart existence only.
/// </para>
/// <para>
/// <b>Party name resolution.</b> Vendor names are resolved via
/// <see cref="IPartyReadModel.GetManyAsync"/>. When a party is missing
/// or tombstoned the <see cref="PartyId.Value"/> string is used as the
/// label (degraded display, not an error).
/// </para>
/// </remarks>
public sealed class ApAgingSummaryCartridge
    : IReportCartridge<ApAgingSummaryParameters, ApAgingSummaryResult>
{
    private readonly IApAgingService _aging;
    private readonly IPartyReadModel _parties;

    /// <summary>Construct bound to the AP aging service and party read model.</summary>
    public ApAgingSummaryCartridge(
        IApAgingService aging,
        IPartyReadModel parties)
    {
        _aging = aging ?? throw new ArgumentNullException(nameof(aging));
        _parties = parties ?? throw new ArgumentNullException(nameof(parties));
    }

    /// <inheritdoc />
    public ReportKind Kind => ReportKind.ApAgingSummary;

    /// <inheritdoc />
    public async Task<ApAgingSummaryResult> ExecuteAsync(
        ReportExecutionContext context,
        ApAgingSummaryParameters parameters,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        // 1. Parameter validation.
        if (parameters.TopOverdueN < 0 || parameters.TopOverdueN > 100)
            throw new ReportParameterValidationException(
                nameof(parameters.TopOverdueN),
                "TopOverdueN must be in the range 0..100.");

        var asOf = parameters.AsOfDate
            ?? DateOnly.FromDateTime(context.AsOfUtc.UtcDateTime);

        // 2. Fetch the full aging snapshot for the chart in one call.
        //    IApAgingService.GetAgingForChartAsync returns all open-bill rows;
        //    we group in-memory for both vendor and property rollups so the
        //    service is called exactly once (determinism + minimal I/O).
        var summary = await _aging
            .GetAgingForChartAsync(parameters.ChartId, asOf, ct)
            .ConfigureAwait(false);

        var rows = summary.Rows;

        // 3. Apply optional vendor-id filter (post-aggregation; silently excludes).
        if (parameters.VendorIds is { Count: > 0 })
        {
            var allowed = new HashSet<string>(
                parameters.VendorIds.Select(id => id.Value),
                StringComparer.Ordinal);
            rows = rows.Where(r => allowed.Contains(r.VendorId.Value)).ToList();
        }

        // 4. Apply optional property-id filter (post-aggregation; silently excludes).
        //    Note: rows with null PropertyId pass through unless PropertyIds is set
        //    (in which case they are excluded from the filtered view — Unassigned rows
        //    only appear when no property filter is active).
        if (parameters.PropertyIds is { Count: > 0 })
        {
            var allowed = new HashSet<string>(parameters.PropertyIds, StringComparer.Ordinal);
            rows = rows.Where(r => r.PropertyId is not null && allowed.Contains(r.PropertyId)).ToList();
        }

        // 5. Build vendor rollup.
        var vendorGroups = rows
            .GroupBy(r => r.VendorId.Value, StringComparer.Ordinal)
            .ToList();

        // Resolve vendor display names in bulk.
        var vendorPartyIds = vendorGroups
            .Select(g => new PartyId(g.Key))
            .ToList();
        var partyMap = await _parties
            .GetManyAsync(vendorPartyIds, ct)
            .ConfigureAwait(false);

        var byVendor = vendorGroups
            .Select(g =>
            {
                var partyId = new PartyId(g.Key);
                var label = partyMap.TryGetValue(partyId, out var party)
                    ? party.DisplayName
                    : g.Key;
                return SumRows(g.Key, label, g);
            })
            .OrderBy(r => r.GroupKey, StringComparer.Ordinal)
            .ToList();

        // 6. Build property rollup.
        var propertyGroups = rows
            .GroupBy(r => r.PropertyId ?? string.Empty, StringComparer.Ordinal)
            .ToList();

        var byProperty = propertyGroups
            .Select(g =>
            {
                var key = g.Key.Length == 0 ? "Unassigned" : g.Key;
                return SumRows(key, key, g);
            })
            // "Unassigned" sorts last.
            .OrderBy(r => r.GroupKey == "Unassigned" ? 1 : 0)
            .ThenBy(r => r.GroupKey, StringComparer.Ordinal)
            .ToList();

        // 7. Portfolio totals — sum over the (filtered) vendor rollup so vendor
        //    and total are always consistent.
        var totals = SumRows("All", "All", rows);

        // 8. Top-N overdue vendors — ranked by 90+ balance descending, then total open descending.
        var topOverdue = parameters.TopOverdueN == 0
            ? (IReadOnlyList<TopOverdueVendor>)Array.Empty<TopOverdueVendor>()
            : byVendor
                .Where(r => r.Days90Plus > 0m)
                .OrderByDescending(r => r.Days90Plus)
                .ThenByDescending(r => r.TotalOpen)
                .Take(parameters.TopOverdueN)
                .Select(r => new TopOverdueVendor(
                    VendorId: new PartyId(r.GroupKey),
                    VendorName: r.GroupLabel,
                    Days90PlusBalance: r.Days90Plus,
                    TotalOpenBalance: r.TotalOpen))
                .ToList();

        return new ApAgingSummaryResult(
            ChartId: parameters.ChartId,
            AsOf: asOf,
            ByVendor: byVendor,
            ByProperty: byProperty,
            Totals: totals,
            TopOverdue: topOverdue);
    }

    // ──────────────────────────────────────────────────────────────────
    //  Helpers
    // ──────────────────────────────────────────────────────────────────

    private static ApAgingSummaryRow SumRows(
        string key,
        string label,
        IEnumerable<AgingRow> source)
    {
        decimal current = 0m, d0to30 = 0m, d31to60 = 0m, d61to90 = 0m, d90plus = 0m;
        foreach (var row in source)
        {
            switch (row.Bucket)
            {
                case AgingBucket.Current:    current += row.Balance; break;
                case AgingBucket.Days0To30:  d0to30  += row.Balance; break;
                case AgingBucket.Days31To60: d31to60 += row.Balance; break;
                case AgingBucket.Days61To90: d61to90 += row.Balance; break;
                case AgingBucket.Days90Plus: d90plus += row.Balance; break;
            }
        }
        return new ApAgingSummaryRow(
            GroupKey: key,
            GroupLabel: label,
            Current: current,
            Days0To30: d0to30,
            Days31To60: d31to60,
            Days61To90: d61to90,
            Days90Plus: d90plus,
            TotalOpen: current + d0to30 + d31to60 + d61to90 + d90plus);
    }
}
