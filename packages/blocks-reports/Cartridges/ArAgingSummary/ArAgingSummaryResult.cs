using System.Collections.Generic;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.People.Foundation.Models;

namespace Harborline.Api.Blocks.Reports.Cartridges.ArAgingSummary;

/// <summary>
/// AR aging summary result — per-customer rollup, per-property rollup,
/// portfolio totals, and top-N most-delinquent customers.
/// </summary>
/// <remarks>
/// W#72 PR 3 per Stage 02 §4.14. All monetary values are in the
/// chart's currency (decimal, never floating-point).
/// </remarks>
/// <param name="ChartId">The AR book this result was computed from.</param>
/// <param name="AsOf">Snapshot date used to classify aging buckets.</param>
/// <param name="ByCustomer">
/// One row per customer with a non-zero open balance, ordered by
/// <see cref="ArAgingSummaryRow.GroupKey"/> (ordinal ascending).
/// </param>
/// <param name="ByProperty">
/// One row per property (plus an "Unassigned" row for invoices with a
/// null <c>PropertyId</c>), ordered by
/// <see cref="ArAgingSummaryRow.GroupKey"/> (ordinal ascending, with
/// "Unassigned" sorted last).
/// </param>
/// <param name="Totals">Portfolio-level sum across all customers.</param>
/// <param name="TopDelinquent">
/// Top-N customers ranked descending by 90+ balance then by total
/// open balance. Empty when <see cref="ArAgingSummaryParameters.TopDelinquentN"/>
/// is 0 or no 90+ balance exists.
/// </param>
public sealed record ArAgingSummaryResult(
    ChartOfAccountsId ChartId,
    System.DateOnly AsOf,
    IReadOnlyList<ArAgingSummaryRow> ByCustomer,
    IReadOnlyList<ArAgingSummaryRow> ByProperty,
    ArAgingSummaryRow Totals,
    IReadOnlyList<TopDelinquentCustomer> TopDelinquent);

/// <summary>
/// One row in the AR aging summary — either a single customer, a
/// single property, or the portfolio total.
/// </summary>
/// <param name="GroupKey">
/// Stable string key: customer <see cref="PartyId.Value"/>, property id
/// string, "Unassigned" for invoices with no property, or "All" for the
/// portfolio total row.
/// </param>
/// <param name="GroupLabel">Human-readable label (customer name or property id or "Unassigned" / "All").</param>
/// <param name="Current">Sum of open-invoice balances that are not yet past due.</param>
/// <param name="Days0To30">Sum in 1–30 days past due.</param>
/// <param name="Days31To60">Sum in 31–60 days past due.</param>
/// <param name="Days61To90">Sum in 61–90 days past due.</param>
/// <param name="Days90Plus">Sum in 91+ days past due.</param>
/// <param name="TotalOpen">Convenience sum of all five buckets.</param>
public sealed record ArAgingSummaryRow(
    string GroupKey,
    string GroupLabel,
    decimal Current,
    decimal Days0To30,
    decimal Days31To60,
    decimal Days61To90,
    decimal Days90Plus,
    decimal TotalOpen);

/// <summary>
/// A top-delinquent customer entry — ranked by 90+ balance descending,
/// then by total open balance descending.
/// </summary>
/// <param name="CustomerId">The customer's party identity.</param>
/// <param name="CustomerName">Display name resolved from <c>IPartyReadModel</c>; falls back to <see cref="CustomerId"/> value when resolution fails.</param>
/// <param name="Days90PlusBalance">The customer's total 90+ bucket balance.</param>
/// <param name="TotalOpenBalance">The customer's total open balance across all buckets.</param>
public sealed record TopDelinquentCustomer(
    PartyId CustomerId,
    string CustomerName,
    decimal Days90PlusBalance,
    decimal TotalOpenBalance);
