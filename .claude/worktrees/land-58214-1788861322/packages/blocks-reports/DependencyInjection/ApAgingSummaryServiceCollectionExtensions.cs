using Microsoft.Extensions.DependencyInjection;
using Harborline.Api.Blocks.Reports.Cartridges.ApAgingSummary;

namespace Harborline.Api.Blocks.Reports.DependencyInjection;

/// <summary>
/// DI extension for the AP Aging Summary cartridge.
/// Mirrors <see cref="ArAgingSummaryServiceCollectionExtensions"/> for the payable side.
/// </summary>
/// <remarks>
/// Call AFTER
/// <see cref="ReportSubstrateServiceCollectionExtensions.AddBlocksReportsSubstrate"/>
/// and AFTER the upstream cluster registrations
/// (<c>AddBlocksFinancialAp</c>, <c>AddBlocksPeopleFoundation</c>).
/// The umbrella <c>AddBlocksReports()</c> chains all cartridge
/// registrations together for hosts that want the full cluster wired
/// in one call.
/// </remarks>
public static class ApAgingSummaryServiceCollectionExtensions
{
    /// <summary>
    /// Register the AP Aging Summary cartridge + its
    /// <see cref="ICartridgeRegistrar"/>.
    /// </summary>
    public static IServiceCollection AddApAgingSummaryCartridge(
        this IServiceCollection services)
    {
        if (services is null) throw new System.ArgumentNullException(nameof(services));

        services.AddSingleton<ApAgingSummaryCartridge>();

        services.AddSingleton<IReportCartridge<ApAgingSummaryParameters, ApAgingSummaryResult>>(
            sp => sp.GetRequiredService<ApAgingSummaryCartridge>());

        services.AddSingleton<ICartridgeRegistrar>(sp =>
            new CartridgeRegistrar<ApAgingSummaryParameters, ApAgingSummaryResult>(
                sp.GetRequiredService<ApAgingSummaryCartridge>()));

        return services;
    }
}
