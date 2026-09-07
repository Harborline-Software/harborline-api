using System.Collections.Generic;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.People.Foundation.Models;

namespace Harborline.Api.Blocks.Reports.Cartridges.ApAgingSummary;

/// <summary>
/// AP aging summary result — per-vendor rollup, per-property rollup,
/// portfolio totals, and top-N most-overdue vendors.
/// </summary>
/// <remarks>
/// Mirrors AR's <see cref="Cartridges.ArAgingSummary.ArAgingSummaryResult"/>
/// with vendor/supplier semantics (bill→invoice, vendor→customer).
/// All monetary values are in the chart's currency (decimal, never floating-point).
/// </remarks>
/// <param name="ChartId">The AP book this result was computed from.</param>
/// <param name="AsOf">Snapshot date used to classify aging buckets.</param>
/// <param name="ByVendor">
/// One row per vendor with a non-zero open balance, ordered by
/// <see cref="ApAgingSummaryRow.GroupKey"/> (ordinal ascending).
/// </param>
/// <param name="ByProperty">
/// One row per property (plus an "Unassigned" row for bills with a
/// null <c>PropertyId</c>), ordered by
/// <see cref="ApAgingSummaryRow.GroupKey"/> (ordinal ascending, with
/// "Unassigned" sorted last).
/// </param>
/// <param name="Totals">Portfolio-level sum across all vendors.</param>
/// <param name="TopOverdue">
/// Top-N vendors ranked descending by 90+ balance then by total
/// open balance. Empty when <see cref="ApAgingSummaryParameters.TopOverdueN"/>
/// is 0 or no 90+ balance exists.
/// </param>
public sealed record ApAgingSummaryResult(
    ChartOfAccountsId ChartId,
    System.DateOnly AsOf,
    IReadOnlyList<ApAgingSummaryRow> ByVendor,
    IReadOnlyList<ApAgingSummaryRow> ByProperty,
    ApAgingSummaryRow Totals,
    IReadOnlyList<TopOverdueVendor> TopOverdue);

/// <summary>
/// One row in the AP aging summary — either a single vendor, a
/// single property, or the portfolio total.
/// </summary>
/// <param name="GroupKey">
/// Stable string key: vendor <see cref="PartyId.Value"/>, property id
/// string, "Unassigned" for bills with no property, or "All" for the
/// portfolio total row.
/// </param>
/// <param name="GroupLabel">Human-readable label (vendor name or property id or "Unassigned" / "All").</param>
/// <param name="Current">Sum of open-bill balances that are not yet past due.</param>
/// <param name="Days0To30">Sum in 1–30 days past due.</param>
/// <param name="Days31To60">Sum in 31–60 days past due.</param>
/// <param name="Days61To90">Sum in 61–90 days past due.</param>
/// <param name="Days90Plus">Sum in 91+ days past due.</param>
/// <param name="TotalOpen">Convenience sum of all five buckets.</param>
public sealed record ApAgingSummaryRow(
    string GroupKey,
    string GroupLabel,
    decimal Current,
    decimal Days0To30,
    decimal Days31To60,
    decimal Days61To90,
    decimal Days90Plus,
    decimal TotalOpen);

/// <summary>
/// A top-overdue vendor entry — ranked by 90+ balance descending,
/// then by total open balance descending.
/// </summary>
/// <param name="VendorId">The vendor's party identity.</param>
/// <param name="VendorName">Display name resolved from <c>IPartyReadModel</c>; falls back to <see cref="VendorId"/> value when resolution fails.</param>
/// <param name="Days90PlusBalance">The vendor's total 90+ bucket balance.</param>
/// <param name="TotalOpenBalance">The vendor's total open balance across all buckets.</param>
public sealed record TopOverdueVendor(
    PartyId VendorId,
    string VendorName,
    decimal Days90PlusBalance,
    decimal TotalOpenBalance);
