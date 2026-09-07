using Harborline.Api.Blocks.Leases.Models;

namespace Harborline.Api.Blocks.Reports.Cartridges.RentRoll;

// Alias to resolve the ambiguity between Harborline.Api.Blocks.Leases.Models.PartyId
// and Harborline.Api.Blocks.People.Foundation.Models.PartyId (both pulled in transitively
// via blocks-leases ProjectReference).
using FoundationPartyId = Harborline.Api.Blocks.People.Foundation.Models.PartyId;

/// <summary>
/// Rent roll v2 result — per-property unit snapshot with occupancy,
/// current rent, delinquency aging, and portfolio totals.
/// Per Stage 02 §4.1 and W#72 hand-off §"PR 6 — Result".
/// </summary>
/// <param name="AsOf">Snapshot date.</param>
/// <param name="Properties">One block per distinct property authority key, ordered by key then by unit label.</param>
/// <param name="Portfolio">Portfolio-level summary across all returned property blocks.</param>
public sealed record RentRollResult(
    DateOnly AsOf,
    IReadOnlyList<RentRollPropertyBlock> Properties,
    RentRollPortfolioSummary Portfolio);

/// <summary>
/// One property's worth of unit rows + a property-level summary.
/// </summary>
/// <param name="PropertyKey">
/// Authority segment of the units' <see cref="Harborline.Api.Foundation.Assets.Common.EntityId"/>
/// (or the literal <c>"no-property"</c> for leases whose UnitId has no authority).
/// </param>
/// <param name="PropertyName">Display label — same as <paramref name="PropertyKey"/> in this substrate pass; updated when property-management cluster ships.</param>
/// <param name="Units">Per-unit rows ordered by <see cref="RentRollUnitRow.UnitLabel"/> ascending.</param>
/// <param name="Summary">Aggregated property-level metrics.</param>
public sealed record RentRollPropertyBlock(
    string PropertyKey,
    string PropertyName,
    IReadOnlyList<RentRollUnitRow> Units,
    RentRollPropertySummary Summary);

/// <summary>
/// One unit's rent-roll row.
/// </summary>
/// <param name="UnitLabel">Display label derived from the unit's <c>EntityId.LocalPart</c>.</param>
/// <param name="CurrentLeaseId">Active lease identifier, or null when vacant.</param>
/// <param name="TenantId">First (primary) tenant party on the current lease (a <see cref="FoundationPartyId"/>), or null when vacant.</param>
/// <param name="TenantName">Resolved display name from <c>IPartyReadModel</c>, or <c>TenantId.Value</c> as fallback. Null when vacant.</param>
/// <param name="LeaseStart">Lease start date, or null when vacant.</param>
/// <param name="LeaseEnd">Lease end date, or null when vacant.</param>
/// <param name="ExpiringSoon">True when <c>LeaseEnd &lt;= AsOfDate + ExpiringWindowDays</c>.</param>
/// <param name="MonthlyRent">Current monthly rent from the active lease, or 0 when vacant.</param>
/// <param name="ProjectedNextMonthRent">Equal to <paramref name="MonthlyRent"/> in this v2 pass (no escalator). See hand-off "Do NOT" note.</param>
/// <param name="LastPaymentDate">
/// Not yet derivable from the current substrate. Always null in this pass.
/// <c>// TODO(cross-cluster): populate from payment history once blocks-financial-ar exposes per-lease payment events.</c>
/// </param>
/// <param name="PrepaidBalance">
/// Not yet derivable from the current substrate. Always 0 in this pass.
/// <c>// TODO(cross-cluster): populate from prepaid ledger entries once the AR prepaid surface ships.</c>
/// </param>
/// <param name="OpenBalance">Sum of open AR invoice balances for this tenant in this chart. 0 when no invoices or no tenant.</param>
/// <param name="DelinquencyBucket">AR aging bucket derived from open invoices for the primary tenant. <see cref="ArAgingBucket.NoBalance"/> when no open invoices.</param>
/// <param name="Status">Current occupancy status.</param>
/// <param name="VacancyReason">Vacancy reason when <paramref name="Status"/> is not <see cref="OccupancyStatus.Occupied"/>, otherwise null.</param>
public sealed record RentRollUnitRow(
    string UnitLabel,
    LeaseId? CurrentLeaseId,
    FoundationPartyId? TenantId,
    string? TenantName,
    DateOnly? LeaseStart,
    DateOnly? LeaseEnd,
    bool ExpiringSoon,
    decimal MonthlyRent,
    decimal ProjectedNextMonthRent,
    DateOnly? LastPaymentDate,
    decimal PrepaidBalance,
    decimal OpenBalance,
    ArAgingBucket DelinquencyBucket,
    OccupancyStatus Status,
    VacancyReason? VacancyReason);

/// <summary>
/// Occupancy status of a unit at the snapshot date.
/// </summary>
public enum OccupancyStatus
{
    /// <summary>A current active or executed lease covers the unit on the as-of date.</summary>
    Occupied,

    /// <summary>No current lease covers the unit on the as-of date.</summary>
    Vacant,

    /// <summary>Tenant has given notice; the lease is still active but ending within the expiring window.</summary>
    NoticeGiven,

    /// <summary>Unit is intentionally taken off market (no leases + inferred from lease-phase history).</summary>
    OffMarket,
}

/// <summary>
/// AR aging bucket for a tenant's open invoice balance.
/// </summary>
public enum ArAgingBucket
{
    /// <summary>Balance exists, not yet past due.</summary>
    Current,

    /// <summary>1–30 days past due.</summary>
    Days0To30,

    /// <summary>31–60 days past due.</summary>
    Days31To60,

    /// <summary>61–90 days past due.</summary>
    Days61To90,

    /// <summary>91+ days past due.</summary>
    Days90Plus,

    /// <summary>No open balance on record.</summary>
    NoBalance,
}

/// <summary>
/// Reason a unit is not occupied at the snapshot date.
/// Approximated from <see cref="Harborline.Api.Blocks.Leases.Models.LeasePhase"/> in this substrate pass.
/// </summary>
public enum VacancyReason
{
    /// <summary>Most recent lease ended at term (Terminated phase).</summary>
    EndOfTerm,

    /// <summary>Most recent lease was cancelled (Cancelled phase) — approximated as turnover.</summary>
    Turnover,

    /// <summary>Lease was terminated prematurely — not yet distinguishable from EndOfTerm in the current substrate.</summary>
    Eviction,

    /// <summary>No lease history exists for this unit.</summary>
    NeverLeased,

    /// <summary>Unit is intentionally off-market.</summary>
    OffMarket,
}

/// <summary>
/// Property-level summary metrics.
/// </summary>
/// <param name="TotalUnits">Total unit count for this property block.</param>
/// <param name="OccupiedUnits">Count of units with <see cref="OccupancyStatus.Occupied"/>.</param>
/// <param name="OccupancyRate">OccupiedUnits / TotalUnits. Zero when TotalUnits == 0.</param>
/// <param name="MonthlyRentTotal">Sum of <see cref="RentRollUnitRow.MonthlyRent"/> across all unit rows.</param>
/// <param name="MonthlyRentTotalIfFullyLeased">Same as MonthlyRentTotal in this pass (no market-rate data). Future: market rent × vacant units.</param>
/// <param name="OpenBalanceTotal">Sum of open AR balances across all occupied units.</param>
public sealed record RentRollPropertySummary(
    int TotalUnits,
    int OccupiedUnits,
    decimal OccupancyRate,
    decimal MonthlyRentTotal,
    decimal MonthlyRentTotalIfFullyLeased,
    decimal OpenBalanceTotal);

/// <summary>
/// Portfolio-level summary across all returned <see cref="RentRollPropertyBlock"/> entries.
/// </summary>
/// <param name="PropertiesCovered">Count of property blocks in the result.</param>
/// <param name="TotalUnits">Sum of TotalUnits across all blocks.</param>
/// <param name="OccupiedUnits">Sum of OccupiedUnits across all blocks.</param>
/// <param name="OccupancyRate">OccupiedUnits / TotalUnits. Zero when TotalUnits == 0.</param>
/// <param name="MonthlyRentTotal">Sum of MonthlyRentTotal across all blocks.</param>
/// <param name="OpenBalanceTotal">Sum of OpenBalanceTotal across all blocks.</param>
public sealed record RentRollPortfolioSummary(
    int PropertiesCovered,
    int TotalUnits,
    int OccupiedUnits,
    decimal OccupancyRate,
    decimal MonthlyRentTotal,
    decimal OpenBalanceTotal);
