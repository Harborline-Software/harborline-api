using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Harborline.Api.Blocks.Reports.Cartridges.ProfitAndLoss;

namespace Harborline.Api.Blocks.Reports.DependencyInjection;

/// <summary>
/// DI extension for the generic entity-level Profit and Loss cartridge.
/// Follows the same per-cartridge registrar pattern as
/// <see cref="TrialBalanceServiceCollectionExtensions.AddTrialBalanceCartridge"/>.
/// </summary>
public static class ProfitAndLossServiceCollectionExtensions
{
    /// <summary>
    /// Register the Profit and Loss cartridge + its registrar. Call
    /// AFTER <see cref="ReportSubstrateServiceCollectionExtensions.AddBlocksReportsSubstrate"/>
    /// and AFTER the upstream cluster registrations
    /// (<c>AddBlocksFinancialLedger</c>).
    /// </summary>
    public static IServiceCollection AddProfitAndLossCartridge(this IServiceCollection services)
    {
        if (services is null) throw new System.ArgumentNullException(nameof(services));

        services.AddSingleton<ProfitAndLossCartridge>();

        services.AddSingleton<IReportCartridge<ProfitAndLossParameters, ProfitAndLossResult>>(
            sp => sp.GetRequiredService<ProfitAndLossCartridge>());

        services.AddSingleton<ICartridgeRegistrar>(sp =>
            new CartridgeRegistrar<ProfitAndLossParameters, ProfitAndLossResult>(
                sp.GetRequiredService<ProfitAndLossCartridge>()));

        return services;
    }
}
