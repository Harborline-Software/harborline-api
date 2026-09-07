using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Harborline.Api.Blocks.Reports.Cartridges.BalanceSheet;

namespace Harborline.Api.Blocks.Reports.DependencyInjection;

/// <summary>
/// DI extension for the Balance Sheet cartridge. Follows the same
/// per-cartridge registrar pattern as
/// <see cref="TrialBalanceServiceCollectionExtensions.AddTrialBalanceCartridge"/>.
/// </summary>
public static class BalanceSheetServiceCollectionExtensions
{
    /// <summary>
    /// Register the Balance Sheet cartridge + its registrar. Call
    /// AFTER <see cref="ReportSubstrateServiceCollectionExtensions.AddBlocksReportsSubstrate"/>
    /// and AFTER the upstream cluster registrations
    /// (<c>AddBlocksFinancialLedger</c>, <c>AddBlocksFinancialPeriods</c>).
    /// </summary>
    public static IServiceCollection AddBalanceSheetCartridge(this IServiceCollection services)
    {
        if (services is null) throw new System.ArgumentNullException(nameof(services));

        services.AddSingleton<BalanceSheetCartridge>();

        services.AddSingleton<IReportCartridge<BalanceSheetParameters, BalanceSheetResult>>(
            sp => sp.GetRequiredService<BalanceSheetCartridge>());

        services.AddSingleton<ICartridgeRegistrar>(sp =>
            new CartridgeRegistrar<BalanceSheetParameters, BalanceSheetResult>(
                sp.GetRequiredService<BalanceSheetCartridge>()));

        return services;
    }
}
