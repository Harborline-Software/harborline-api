using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.FinancialLedger.Services;

/// <summary>
/// Cross-entity consolidation read surface (ADR 0105 §2). Fans the shipped,
/// already-tenant+chart-scoped
/// <see cref="IGeneralLedgerReadModel.GetAccountBalancesAsOfAsync"/> out across
/// a SET of charts and returns the result <b>per chart, un-summed</b> — so the
/// consolidation service (ADR 0105 §3, Wave-0 W0-6) can apply elimination rules
/// BEFORE summing.
/// </summary>
/// <remarks>
/// <para>
/// <b>No kernel surface.</b> This is a thin composition over
/// <see cref="IGeneralLedgerReadModel"/> (the report read substrate), NOT the
/// kernel <c>IBalanceProjection</c> (off the report read path; ADR 0105 §1.2,
/// net-arch-2). There is no new kernel interface, no <c>ScopedAccountKey</c>,
/// no adapter, and no posting-stream chart-stamp.
/// </para>
/// <para>
/// <b>Elimination-before-summation is structural.</b> The per-chart un-summed
/// return shape (the chart axis is never collapsed at the read tier) forces the
/// consolidation service to walk per-chart maps, apply <c>EliminationRule</c>s,
/// then sum — enforced by the type, not by convention (ADR 0105 §2.1).
/// </para>
/// </remarks>
public interface IConsolidationReadModel
{
    /// <summary>
    /// Read per-chart signed balances as of <paramref name="asOf"/> for every
    /// chart in <paramref name="charts"/>, returned keyed by
    /// <see cref="ChartOfAccountsId"/> and NOT pre-summed.
    /// </summary>
    /// <remarks>
    /// <b>FAIL-CLOSED tenant invariant (ADR 0105 §2.2, invariant 0105-1 —
    /// load-bearing, must not waive).</b> Every chart in
    /// <paramref name="charts"/> is resolved chart → tenant and MUST belong to
    /// <paramref name="tenantId"/>. Any chart not belonging to the passed tenant
    /// — including an unknown chart — causes the whole request to be
    /// <b>REJECTED (throw)</b>; it is NEVER silently dropped or returned empty,
    /// because a silent-empty would mask a consolidation-scope construction bug
    /// and under-count the group total (a quiet wrong answer is worse than a loud
    /// failure). The underlying per-chart read is itself already tenant-scoped
    /// server-side, so this validation is belt-and-suspenders at the fan-out
    /// boundary where scope is constructed from a caller-supplied list.
    /// </remarks>
    /// <param name="tenantId">Tenant scope. Every requested chart must belong to this tenant.</param>
    /// <param name="charts">Charts to fan out over (one chart == one legal entity, ADR 0104). A chart named more than once is read once.</param>
    /// <param name="asOf">Cutoff date (inclusive), forwarded to each per-chart read.</param>
    /// <param name="snapshotMarker">Opaque snapshot marker forwarded from the report-runner context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Per-chart, un-summed signed balance maps keyed by <see cref="ChartOfAccountsId"/>.</returns>
    Task<IReadOnlyDictionary<ChartOfAccountsId, IReadOnlyDictionary<GLAccountId, decimal>>>
        GetBalancesForChartsAsync(
            TenantId tenantId,
            IReadOnlyList<ChartOfAccountsId> charts,
            System.DateOnly asOf,
            string snapshotMarker,
            CancellationToken cancellationToken = default);

    /// <summary>
    /// Fan the counterparty-grained inter-company read
    /// (<see cref="IGeneralLedgerReadModel.GetIntercompanyBalancesAsOfAsync"/>) out
    /// across <paramref name="charts"/>, returned per chart and NOT pre-summed —
    /// the counterparty-preserving sibling of <see cref="GetBalancesForChartsAsync"/>
    /// that the consolidation service (ADR 0105 §3.2.1) needs to decide which inter-
    /// company balances are in-scope.
    /// </summary>
    /// <remarks>
    /// <b>SAME fail-closed chart∈tenant gate (ADR 0105 §2.2, invariant 0105-1 —
    /// load-bearing).</b> This fan-out reuses the exact same gate as
    /// <see cref="GetBalancesForChartsAsync"/>: every requested chart is validated
    /// chart → tenant BEFORE any read, and any foreign / unknown chart REJECTS the whole
    /// request (throw) rather than silently dropping it. The gate is not forked — the
    /// implementation runs the identical validation both fan-outs call.
    /// </remarks>
    /// <returns>
    /// Per chart, per account, the signed inter-company balance broken down by
    /// counterparty entity. Charts / accounts with no counterparty-stamped activity yield
    /// empty inner maps.
    /// </returns>
    Task<IReadOnlyDictionary<ChartOfAccountsId, IReadOnlyDictionary<GLAccountId, IReadOnlyDictionary<LegalEntityId, decimal>>>>
        GetIntercompanyBalancesForChartsAsync(
            TenantId tenantId,
            IReadOnlyList<ChartOfAccountsId> charts,
            System.DateOnly asOf,
            string snapshotMarker,
            CancellationToken cancellationToken = default);
}
