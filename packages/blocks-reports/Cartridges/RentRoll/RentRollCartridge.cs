using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Harborline.Api.Blocks.FinancialAr.Models;
using Harborline.Api.Blocks.FinancialAr.Services;
using Harborline.Api.Blocks.FinancialLedger.Models;   // ADR 0120 PR-E2: AgingBucket canonical in ledger
using Harborline.Api.Blocks.Leases.Models;
using Harborline.Api.Blocks.Leases.Services;
using Harborline.Api.Blocks.People.Foundation.Services;
using Harborline.Api.Blocks.Reports.Exceptions;

// Alias to resolve ambiguity between Harborline.Api.Blocks.Leases.Models.PartyId / Party
// and Harborline.Api.Blocks.People.Foundation.Models.PartyId / Party, both pulled in
// transitively via blocks-leases ProjectReference.
using FoundationParty = Harborline.Api.Blocks.People.Foundation.Models.Party;
using FoundationPartyId = Harborline.Api.Blocks.People.Foundation.Models.PartyId;

namespace Harborline.Api.Blocks.Reports.Cartridges.RentRoll;

/// <summary>
/// W#72 PR 6 — Rent Roll v2 cartridge per Stage 02 §4.1.
/// Produces a per-property snapshot of every unit, its active lease,
/// tenant, current rent, open AR balance, delinquency aging bucket,
/// and portfolio-level totals.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read-side discipline.</b> No writes, no event publication.
/// The cartridge is a pure orchestrator over read-side services.
/// </para>
///
/// <para>
/// <b>Substrate deviations from hand-off spec (documented per PR 5 deviation pattern).</b>
/// </para>
///
/// <para>
/// D1 — No <c>PropertyId</c> on <see cref="Lease"/>. The substrate stores <c>UnitId</c>
/// (<see cref="Harborline.Api.Foundation.Assets.Common.EntityId"/> with scheme <c>unit:authority/localPart</c>).
/// The <c>UnitId.Authority</c> segment is used as the property grouping key.
/// The spec's <c>IPropertyReadModel.GetUnitsAsync</c> is not implemented; units are
/// discovered by enumerating tenant leases and grouping by <c>UnitId.Authority</c>.
/// When the property-management cluster ships, this cartridge will migrate to an
/// explicit <c>PropertyId</c> field.
/// </para>
///
/// <para>
/// D2 — No <c>GetCurrentLeaseForUnitAsync</c> on <see cref="ILeaseService"/>. The
/// "active on as-of date" lease for a unit is derived inline: among all leases for
/// the unit (same <c>UnitId</c>), the one whose <c>StartDate &lt;= asOf &lt;= EndDate</c>
/// and whose <c>Phase</c> is <see cref="LeasePhase.Active"/> or
/// <see cref="LeasePhase.Executed"/> is selected. If multiple qualify, the one with
/// the latest <c>StartDate</c> wins.
/// </para>
///
/// <para>
/// D3 — No per-lease AR aging scope. <see cref="IArAgingService.GetAgingForChartAsync"/>
/// returns all open invoices for the chart; the cartridge groups them by
/// <see cref="AgingRow.CustomerId"/> and joins to the primary tenant
/// (<c>Lease.Tenants[0]</c>) to derive <c>OpenBalance</c> and
/// <see cref="ArAgingBucket"/>.
/// </para>
///
/// <para>
/// D4 — <c>PrepaidBalance</c> and <c>LastPaymentDate</c> are not derivable from the
/// current substrate. They are stubbed to <c>0m</c> / <c>null</c> respectively.
/// <c>// TODO(cross-cluster): populate from payment history once blocks-financial-ar
/// exposes per-lease payment events.</c>
/// </para>
///
/// <para>
/// D5 — <c>VacancyReason</c> is approximated from <see cref="LeasePhase"/>:
/// <see cref="LeasePhase.Terminated"/> → <see cref="VacancyReason.EndOfTerm"/>,
/// <see cref="LeasePhase.Cancelled"/> → <see cref="VacancyReason.Turnover"/>,
/// no prior lease → <see cref="VacancyReason.NeverLeased"/>.
/// Eviction is not yet distinguishable at this substrate level.
/// </para>
/// </remarks>
public sealed class RentRollCartridge
    : IReportCartridge<RentRollParameters, RentRollResult>
{
    private readonly ILeaseService _leases;
    private readonly IArAgingService _aging;
    private readonly IPartyReadModel _parties;

    /// <summary>Construct bound to the lease service, AR aging service, and party read model.</summary>
    public RentRollCartridge(
        ILeaseService leases,
        IArAgingService aging,
        IPartyReadModel parties)
    {
        _leases  = leases  ?? throw new ArgumentNullException(nameof(leases));
        _aging   = aging   ?? throw new ArgumentNullException(nameof(aging));
        _parties = parties ?? throw new ArgumentNullException(nameof(parties));
    }

    /// <inheritdoc />
    public ReportKind Kind => ReportKind.RentRoll;

    /// <inheritdoc />
    public async Task<RentRollResult> ExecuteAsync(
        ReportExecutionContext context,
        RentRollParameters parameters,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        // Parameter validation.
        if (parameters.ExpiringWindowDays < 0)
            throw new ReportParameterValidationException(
                nameof(parameters.ExpiringWindowDays),
                "ExpiringWindowDays must be >= 0.");

        var asOf = parameters.AsOfDate
            ?? DateOnly.FromDateTime(context.AsOfUtc.UtcDateTime);

        // 1. Load all tenant leases in one streaming pass.
        var allLeases = await CollectLeasesAsync(
            new ListLeasesQuery { TenantId = context.TenantId },
            ct).ConfigureAwait(false);

        // 2. Group by property (UnitId.Authority). D1 deviation.
        var byProperty = allLeases
            .GroupBy(l => ExtractPropertyKey(l.UnitId))
            .ToList();

        // 3. Apply optional property filter.
        if (parameters.PropertyAuthorityKeys is { Count: > 0 })
        {
            var allowed = new HashSet<string>(
                parameters.PropertyAuthorityKeys,
                StringComparer.Ordinal);
            byProperty = byProperty.Where(g => allowed.Contains(g.Key)).ToList();
        }

        // 4. Pre-fetch chart-wide AR aging snapshot. D3 deviation.
        //    Groups open invoice balances by CustomerId for join to lease tenants.
        var agingByTenant = await BuildAgingByTenantAsync(
            parameters.ChartId,
            asOf,
            ct).ConfigureAwait(false);

        // 5. Resolve all tenant party names in bulk. Collect all PartyIds first.
        var allTenantIds = allLeases
            .SelectMany(l => l.Tenants)
            .Distinct()
            .ToList();
        var partyMap = await _parties
            .GetManyAsync(allTenantIds, ct)
            .ConfigureAwait(false);

        // 6. Build per-property blocks.
        var blocks = new List<RentRollPropertyBlock>();
        int portfolioUnits    = 0;
        int portfolioOccupied = 0;
        decimal portfolioRent = 0m;
        decimal portfolioOpen = 0m;

        foreach (var group in byProperty.OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var propertyKey = group.Key;
            // All leases in this property grouped by UnitId (one row per unit).
            var leasesByUnit = group
                .GroupBy(l => l.UnitId.ToString(), StringComparer.Ordinal)
                .ToList();

            var rows = new List<RentRollUnitRow>();

            foreach (var unitGroup in leasesByUnit.OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var unitLeases = unitGroup.ToList();
                var unitLabel  = ExtractUnitLabel(unitGroup.First().UnitId);

                // D2: Determine the "current" lease — active on the asOf date.
                var currentLease = FindCurrentLease(unitLeases, asOf);

                if (currentLease is null && !parameters.IncludeVacant)
                    continue;

                var row = BuildRow(
                    unitLabel,
                    currentLease,
                    unitLeases,
                    asOf,
                    parameters.ExpiringWindowDays,
                    agingByTenant,
                    partyMap);

                rows.Add(row);
            }

            var summary = SummarizeProperty(rows);
            blocks.Add(new RentRollPropertyBlock(propertyKey, propertyKey, rows, summary));

            portfolioUnits    += summary.TotalUnits;
            portfolioOccupied += summary.OccupiedUnits;
            portfolioRent     += summary.MonthlyRentTotal;
            portfolioOpen     += summary.OpenBalanceTotal;
        }

        var portfolio = new RentRollPortfolioSummary(
            PropertiesCovered: blocks.Count,
            TotalUnits:        portfolioUnits,
            OccupiedUnits:     portfolioOccupied,
            OccupancyRate:     portfolioUnits == 0
                ? 0m
                : (decimal)portfolioOccupied / portfolioUnits,
            MonthlyRentTotal:  portfolioRent,
            OpenBalanceTotal:  portfolioOpen);

        return new RentRollResult(asOf, blocks, portfolio);
    }

    // ──────────────────────────────────────────────────────────────────
    //  Helpers
    // ──────────────────────────────────────────────────────────────────

    private async Task<List<Lease>> CollectLeasesAsync(
        ListLeasesQuery query,
        CancellationToken ct)
    {
        var result = new List<Lease>();
        await foreach (var lease in _leases.ListAsync(query, ct).WithCancellation(ct).ConfigureAwait(false))
            result.Add(lease);
        return result;
    }

    /// <summary>
    /// D3: Build a map of CustomerId (primary tenant PartyId) → (OpenBalance, WorstBucket).
    /// Calls <see cref="IArAgingService.GetAgingForChartAsync"/> once and groups by CustomerId.
    /// </summary>
    private async Task<Dictionary<string, (decimal OpenBalance, ArAgingBucket Bucket)>>
        BuildAgingByTenantAsync(
            Harborline.Api.Blocks.FinancialLedger.Models.ChartOfAccountsId chartId,
            DateOnly asOf,
            CancellationToken ct)
    {
        AgingSummary summary;
        try
        {
            summary = await _aging.GetAgingForChartAsync(chartId, asOf, ct).ConfigureAwait(false);
        }
        catch
        {
            // Degraded path: if the AR aging call fails, return empty map.
            // Cartridge still produces a result (with NoBalance buckets) rather than failing.
            return new Dictionary<string, (decimal, ArAgingBucket)>(StringComparer.Ordinal);
        }

        var map = new Dictionary<string, (decimal, ArAgingBucket)>(StringComparer.Ordinal);
        foreach (var row in summary.Rows)
        {
            var key = row.CustomerId.Value;
            if (!map.TryGetValue(key, out var existing))
            {
                map[key] = (row.Balance, MapBucket(row.Bucket));
                continue;
            }
            // Accumulate balance; keep worst (highest) bucket.
            var newBucket = WorstBucket(existing.Item2, MapBucket(row.Bucket));
            map[key] = (existing.Item1 + row.Balance, newBucket);
        }
        return map;
    }

    private static Lease? FindCurrentLease(IReadOnlyList<Lease> unitLeases, DateOnly asOf)
    {
        // D2: "Active on asOf" = StartDate <= asOf <= EndDate AND phase is Active or Executed.
        return unitLeases
            .Where(l =>
                (l.Phase == LeasePhase.Active || l.Phase == LeasePhase.Executed) &&
                l.StartDate <= asOf &&
                l.EndDate   >= asOf)
            .OrderByDescending(l => l.StartDate)
            .FirstOrDefault();
    }

    private static RentRollUnitRow BuildRow(
        string unitLabel,
        Lease? current,
        IReadOnlyList<Lease> allUnitLeases,
        DateOnly asOf,
        int expiringWindowDays,
        Dictionary<string, (decimal OpenBalance, ArAgingBucket Bucket)> agingByTenant,
        IReadOnlyDictionary<FoundationPartyId, FoundationParty> partyMap)
    {
        if (current is null)
        {
            // Vacant unit.
            var reason = DetermineVacancyReason(allUnitLeases);
            return new RentRollUnitRow(
                UnitLabel:             unitLabel,
                CurrentLeaseId:        null,
                TenantId:              null,
                TenantName:            null,
                LeaseStart:            null,
                LeaseEnd:              null,
                ExpiringSoon:          false,
                MonthlyRent:           0m,
                ProjectedNextMonthRent: 0m,
                LastPaymentDate:       null,   // D4: TODO(cross-cluster)
                PrepaidBalance:        0m,     // D4: TODO(cross-cluster)
                OpenBalance:           0m,
                DelinquencyBucket:     ArAgingBucket.NoBalance,
                Status:                OccupancyStatus.Vacant,
                VacancyReason:         reason);
        }

        // Occupied: resolve primary tenant.
        var primaryTenantId = current.Tenants.Count > 0 ? current.Tenants[0] : (FoundationPartyId?)null;

        string? tenantName = null;
        decimal openBalance = 0m;
        var delinquencyBucket = ArAgingBucket.NoBalance;

        if (primaryTenantId.HasValue)
        {
            // Party name resolution.
            tenantName = partyMap.TryGetValue(primaryTenantId.Value, out var party)
                ? party.DisplayName
                : primaryTenantId.Value.Value; // fallback: raw id string

            // D3: AR aging join by CustomerId.
            if (agingByTenant.TryGetValue(primaryTenantId.Value.Value, out var aging))
            {
                openBalance       = aging.OpenBalance;
                delinquencyBucket = aging.Bucket;
            }
        }

        var expiringSoon = current.EndDate <= asOf.AddDays(expiringWindowDays);
        var status = expiringSoon
            ? OccupancyStatus.NoticeGiven
            : OccupancyStatus.Occupied;

        return new RentRollUnitRow(
            UnitLabel:              unitLabel,
            CurrentLeaseId:         current.Id,
            TenantId:               primaryTenantId,
            TenantName:             tenantName,
            LeaseStart:             current.StartDate,
            LeaseEnd:               current.EndDate,
            ExpiringSoon:           expiringSoon,
            MonthlyRent:            current.MonthlyRent,
            ProjectedNextMonthRent: current.MonthlyRent, // D4: v2 projects current rent unchanged
            LastPaymentDate:        null,                 // D4: TODO(cross-cluster)
            PrepaidBalance:         0m,                   // D4: TODO(cross-cluster)
            OpenBalance:            openBalance,
            DelinquencyBucket:      delinquencyBucket,
            Status:                 status,
            VacancyReason:          null);
    }

    private static RentRollPropertySummary SummarizeProperty(IReadOnlyList<RentRollUnitRow> rows)
    {
        var total    = rows.Count;
        var occupied = rows.Count(r => r.Status == OccupancyStatus.Occupied
                                    || r.Status == OccupancyStatus.NoticeGiven);
        var rent     = rows.Sum(r => r.MonthlyRent);
        var open     = rows.Sum(r => r.OpenBalance);
        return new RentRollPropertySummary(
            TotalUnits:                   total,
            OccupiedUnits:                occupied,
            OccupancyRate:                total == 0 ? 0m : (decimal)occupied / total,
            MonthlyRentTotal:             rent,
            MonthlyRentTotalIfFullyLeased: rent,   // D4: same as rent; market-rate data not available
            OpenBalanceTotal:             open);
    }

    // D5: Approximate vacancy reason from lease phase history.
    private static VacancyReason DetermineVacancyReason(IReadOnlyList<Lease> allUnitLeases)
    {
        if (allUnitLeases.Count == 0)
            return VacancyReason.NeverLeased;

        // Find the most recently ended lease.
        var mostRecent = allUnitLeases
            .OrderByDescending(l => l.EndDate)
            .First();

        return mostRecent.Phase switch
        {
            LeasePhase.Terminated => VacancyReason.EndOfTerm,
            LeasePhase.Cancelled  => VacancyReason.Turnover,
            _                    => VacancyReason.NeverLeased,
        };
    }

    // D1: Extract property grouping key from UnitId.Authority.
    private static string ExtractPropertyKey(Harborline.Api.Foundation.Assets.Common.EntityId unitId)
        => string.IsNullOrEmpty(unitId.Authority) ? "no-property" : unitId.Authority;

    private static string ExtractUnitLabel(Harborline.Api.Foundation.Assets.Common.EntityId unitId)
        => string.IsNullOrEmpty(unitId.LocalPart) ? unitId.ToString() : unitId.LocalPart;

    private static ArAgingBucket MapBucket(AgingBucket bucket) => bucket switch
    {
        AgingBucket.Current    => ArAgingBucket.Current,
        AgingBucket.Days0To30  => ArAgingBucket.Days0To30,
        AgingBucket.Days31To60 => ArAgingBucket.Days31To60,
        AgingBucket.Days61To90 => ArAgingBucket.Days61To90,
        AgingBucket.Days90Plus => ArAgingBucket.Days90Plus,
        _                      => ArAgingBucket.NoBalance,
    };

    private static ArAgingBucket WorstBucket(ArAgingBucket a, ArAgingBucket b)
        => (ArAgingBucket)Math.Max((int)a, (int)b);
}
