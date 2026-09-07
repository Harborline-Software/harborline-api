using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Harborline.Api.Blocks.FinancialAr.Migration;
using Harborline.Api.Blocks.FinancialAr.Models;
using Harborline.Api.Blocks.FinancialAr.Services;
using Harborline.Api.Foundation.Events;
using Harborline.Api.Foundation.Scheduling.DependencyInjection;

namespace Harborline.Api.Blocks.FinancialAr.DependencyInjection;

/// <summary>
/// DI helpers for the accounts-receivable cluster.
/// </summary>
public static class FinancialArServiceCollectionExtensions
{
    /// <summary>
    /// Register the in-memory invoice substrate. Uses
    /// <c>TryAddSingleton</c> so persistence-backed implementations
    /// registered earlier by the host shadow these defaults.
    ///
    /// <para>
    /// Optional <paramref name="configure"/> lets the host set
    /// <see cref="BlocksFinancialArOptions.LocalReplicaId"/>. Without
    /// it the sentinel <c>"AA"</c> is used — fine for single-replica
    /// tests, but installs SHOULD override at boot so two devices on
    /// the same install can't mint colliding invoice numbers.
    /// </para>
    /// </summary>
    public static IServiceCollection AddBlocksFinancialAr(
        this IServiceCollection services,
        Action<BlocksFinancialArOptions>? configure = null)
    {
        var options = new BlocksFinancialArOptions();
        configure?.Invoke(options);

        services.TryAddSingleton(options);
        services.TryAddSingleton<IInvoiceRepository, InMemoryInvoiceRepository>();
        services.TryAddSingleton<IInvoiceNumberingService>(_ =>
            new InMemoryInvoiceNumberingService(options.LocalReplicaId));
        services.TryAddSingleton<ITaxCalculator, NoOpTaxCalculator>();
        services.TryAddSingleton<IDomainEventPublisher, NoopDomainEventPublisher>();
        services.TryAddSingleton<IInvoicePostingService, InvoicePostingService>();
        // Recurring-invoice scheduling — registers IRruleExpansionService +
        // IRecurringInvoiceService (foundation-scheduling + blocks-financial-ar).
        services.AddFoundationScheduling();
        services.TryAddSingleton<IRecurringInvoiceService, InMemoryRecurringInvoiceService>();
        services.TryAddSingleton<IArAgingService, ArAgingService>();
        services.TryAddSingleton<IErpnextSalesInvoiceImporter, ErpnextSalesInvoiceImporter>();
        // A4.1 ERPNext sales-invoice orchestration pass (ADR 0100). Thin
        // orchestrator over the per-record importer; transient.
        services.TryAddTransient<ErpnextSalesInvoicePass>();
        return services;
    }
}
