using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Harborline.Api.Blocks.FinancialAp.Services;
using Harborline.Api.Blocks.FinancialAr.Services;
using Harborline.Api.Blocks.FinancialLedger.Services;
using Harborline.Api.Blocks.FinancialPayments.Migration;
using Harborline.Api.Blocks.FinancialPayments.Models;
using Harborline.Api.Blocks.FinancialPayments.Services;

namespace Harborline.Api.Blocks.FinancialPayments.DependencyInjection;

/// <summary>
/// DI helpers for the financial-payments cluster.
/// </summary>
public static class PaymentsServiceCollectionExtensions
{
    /// <summary>
    /// Register the in-memory payments substrate. Uses <c>TryAddSingleton</c>
    /// so persistence-backed implementations registered earlier by the host
    /// shadow these defaults.
    ///
    /// <para>
    /// Optional <paramref name="configure"/> lets the host override
    /// <see cref="BlocksFinancialPaymentsOptions"/>. Omitting it uses defaults
    /// (5-minute fallback polling interval).
    /// </para>
    ///
    /// <para>
    /// <b>PR 2 / PR 3:</b> <see cref="IPaymentPostingService"/> registers via
    /// <see cref="DefaultPaymentPostingService"/> and
    /// <see cref="IPaymentApplicationService"/> registers via
    /// <see cref="DefaultPaymentApplicationService"/>. The host must also have
    /// the ledger, AR, and AP substrates wired — <see cref="IJournalPostingService"/>
    /// and <see cref="IAccountResolver"/> from the host's posting composition
    /// (<c>AddNodeFinancialPosting</c>), <see cref="IInvoiceRepository"/> from
    /// <c>AddBlocksFinancialAr</c>, and <see cref="IBillRepository"/> from
    /// <c>AddBlocksFinancialAp</c>.
    /// </para>
    ///
    /// <para>
    /// <b>PR 3 amber-amendment:</b> the host MUST also register
    /// <see cref="Harborline.Api.Foundation.MultiTenancy.ITenantContext"/>.
    /// <see cref="DefaultPaymentApplicationService"/> consumes it for
    /// service-level tenant-isolation guards; invoking the service without a
    /// resolved tenant throws <see cref="InvalidOperationException"/>.
    /// </para>
    /// </summary>
    public static IServiceCollection AddHarborlineFinancialPayments(
        this IServiceCollection services,
        Action<BlocksFinancialPaymentsOptions>? configure = null)
    {
        var options = new BlocksFinancialPaymentsOptions();
        configure?.Invoke(options);

        services.TryAddSingleton(options);
        services.TryAddSingleton<IPaymentRepository, InMemoryPaymentRepository>();
        services.TryAddSingleton<IPaymentApplicationRepository, InMemoryPaymentApplicationRepository>();
        services.TryAddScoped<IPaymentPostingService, DefaultPaymentPostingService>();
        services.TryAddScoped<IPaymentApplicationService, DefaultPaymentApplicationService>();

        // A4.3 ERPNext payment importer + orchestration pass (ADR 0100). The
        // importer consumes the tenant-scoped journal-posting boundary, so both it
        // and its thin pass wrapper are scoped. This avoids a captive posting service
        // in hosts whose ledger is backed by a scoped persistence context.
        services.TryAddScoped<IErpnextPaymentImporter, ErpnextPaymentImporter>();
        services.TryAddScoped<ErpnextPaymentPass>();

        return services;
    }
}
