using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.FinancialLedger.Services;

/// <summary>
/// Tenant → <see cref="ChartOfAccounts"/> lookup. Bridges the gap between
/// the tenant-keyed substrate (post-cohort-2 PR 0 cluster) and the
/// chart-keyed repository surface that <c>IInvoiceRepository</c>,
/// <c>IPaymentRepository</c>, etc. expose.
///
/// <para>
/// <b>v1 contract.</b> Returns the tenant's <i>default</i> chart-of-accounts
/// id. v1 implementations assume single-chart-per-tenant; multi-chart
/// support is a forward-watch concern (introduces
/// <c>EnumerateChartsAsync(tenantId)</c> when needed).
/// </para>
///
/// <para>
/// <b>Why this exists.</b> Cohort-2 frontend contracts (earlier app ticket #17/18/19)
/// pass tenantId via <see cref="Harborline.Api.Foundation.Authorization.ITenantContext"/>
/// but NOT chartId per the W#76 Q1 ratification ("tenant scoping is
/// server-derived; frontend does NOT pass tenant parameters"). Bridge
/// handlers consume this service to resolve chartId server-side before
/// calling tenant + chart-keyed substrate methods.
/// </para>
/// </summary>
public interface IChartCatalogService
{
    /// <summary>
    /// Returns the default <see cref="ChartOfAccounts.Id"/> for
    /// <paramref name="tenantId"/>. Returns <c>null</c> when no chart is
    /// registered for the tenant — Bridge handlers MUST treat this as the
    /// uniform-404 surface (empty list / 404 / no-op) per ADR 0092 §A3
    /// (no diagnostic leak between "tenant unknown" and "tenant has no chart").
    /// </summary>
    Task<ChartOfAccountsId?> GetDefaultChartIdAsync(TenantId tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers <paramref name="chartId"/> as the default chart for
    /// <paramref name="tenantId"/>. Used by host composition roots
    /// (signal-bridge Program.cs, Harborline desktop boot, tests) to seed the
    /// per-tenant mapping. Overwrites any prior default for this tenant
    /// (last-write-wins; multi-chart-per-tenant is forward-watch scope).
    /// </summary>
    Task RegisterDefaultChartAsync(TenantId tenantId, ChartOfAccountsId chartId, CancellationToken cancellationToken = default);
}
