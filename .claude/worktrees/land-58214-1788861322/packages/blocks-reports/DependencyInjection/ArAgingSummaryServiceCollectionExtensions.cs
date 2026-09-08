using Microsoft.Extensions.DependencyInjection;
using Harborline.Api.Blocks.Reports.Cartridges.ArAgingSummary;

namespace Harborline.Api.Blocks.Reports.DependencyInjection;

/// <summary>
/// DI extension for the AR Aging Summary cartridge.
/// W#72 PR 3 per Stage 02 §4.14 + hand-off §"PR 3 DI".
/// </summary>
/// <remarks>
/// Call AFTER
/// <see cref="ReportSubstrateServiceCollectionExtensions.AddBlocksReportsSubstrate"/>
/// and AFTER the upstream cluster registrations
/// (<c>AddBlocksFinancialAr</c>, <c>AddBlocksPeopleFoundation</c>).
/// The PR 7 umbrella <c>AddBlocksReports()</c> chains all cartridge
/// registrations together for hosts that want the full cluster wired
/// in one call.
/// </remarks>
public static class ArAgingSummaryServiceCollectionExtensions
{
    /// <summary>
    /// Register the AR Aging Summary cartridge + its
    /// <see cref="ICartridgeRegistrar"/>.
    /// </summary>
    public static IServiceCollection AddArAgingSummaryCartridge(
        this IServiceCollection services)
    {
        if (services is null) throw new System.ArgumentNullException(nameof(services));

        services.AddSingleton<ArAgingSummaryCartridge>();

        services.AddSingleton<IReportCartridge<ArAgingSummaryParameters, ArAgingSummaryResult>>(
            sp => sp.GetRequiredService<ArAgingSummaryCartridge>());

        services.AddSingleton<ICartridgeRegistrar>(sp =>
            new CartridgeRegistrar<ArAgingSummaryParameters, ArAgingSummaryResult>(
                sp.GetRequiredService<ArAgingSummaryCartridge>()));

        return services;
    }
}
