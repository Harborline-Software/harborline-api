using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.FinancialLedger.Services;

/// <summary>
/// Produces a consolidated / combined balance set across the entities in a
/// <see cref="ConsolidationScope"/>, applying the ADR 0105 §3 elimination rules
/// scope-relatively (§3.2.1), rolling equity per the presentation mode (§3.4), and
/// banding the result per financial F-1 (§4.1). The terminal Wave-0 accounting node
/// (W0-6).
/// </summary>
/// <remarks>
/// <para>
/// Resolves each in-scope <see cref="LegalEntityId"/> to its chart
/// (<see cref="IEntityChartResolver"/>), reads per-chart aggregate balances AND the
/// counterparty-grained inter-company slice through the §2 fan-out (which enforces the
/// load-bearing fail-closed chart∈tenant invariant 0105-1), eliminates only matched
/// pairs whose counterparty is itself in-scope, then sums into bands.
/// </para>
/// <para>
/// <b>No balancing plug (financial F-3).</b> A non-tying band surfaces a
/// <see cref="ConsolidationWarningKind.BalanceSheetOutOfBalance"/> warning and renders
/// as-is — never silently forced to zero.
/// </para>
/// <para>
/// <b>One basis per scope (financial F-5).</b> The basis argument applies to the
/// WHOLE scope; mixed-basis rollups cannot be expressed. v1 supports
/// <see cref="ReportingBasis.Accrual"/> only — <see cref="ReportingBasis.Cash"/> is a
/// Wave-3 build and is rejected loudly (the gap is disclosed, not implied parity).
/// </para>
/// <para>
/// <b>Equity-method deferral.</b> A scope member tagged
/// <see cref="ConsolidationPresentation.Equity"/> is rejected with
/// <see cref="NotSupportedException"/> — equity-method consolidation is a documented
/// deferral and the W0-1 resolver never emits that band.
/// </para>
/// </remarks>
public interface IConsolidationService
{
    /// <summary>
    /// Consolidate / combine the scope's entities as of <paramref name="asOf"/>.
    /// </summary>
    /// <param name="tenantId">Tenant scope. Every resolved chart must belong to this tenant (enforced downstream, 0105-1).</param>
    /// <param name="scope">The reporting group: a root entity plus members tagged Consolidated (owned sub) or Combined (common-control sibling).</param>
    /// <param name="asOf">Cutoff date (inclusive), forwarded to each per-chart read.</param>
    /// <param name="snapshotMarker">Opaque snapshot marker forwarded from the report-runner context.</param>
    /// <param name="basis">The single reporting basis for the whole scope. v1: <see cref="ReportingBasis.Accrual"/> only.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="NotSupportedException"><paramref name="basis"/> is <see cref="ReportingBasis.Cash"/> (Wave-3 deferral), or a scope member is tagged <see cref="ConsolidationPresentation.Equity"/> (equity-method deferral).</exception>
    /// <exception cref="InvalidOperationException">An in-scope entity has no resolvable chart, or a resolved chart is not owned by <paramref name="tenantId"/> (0105-1 fail-closed gate).</exception>
    Task<ConsolidatedBalanceSet> ConsolidateAsync(
        TenantId tenantId,
        ConsolidationScope scope,
        System.DateOnly asOf,
        string snapshotMarker,
        ReportingBasis basis,
        CancellationToken cancellationToken = default);
}
