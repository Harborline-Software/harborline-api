using Microsoft.Extensions.DependencyInjection;
using Harborline.Api.Blocks.Reports.Cartridges.RentRoll;

namespace Harborline.Api.Blocks.Reports.DependencyInjection;

/// <summary>
/// DI extension for the Rent Roll v2 cartridge.
/// W#72 PR 6 per Stage 02 §4.1 + hand-off §"PR 6 DI".
/// </summary>
/// <remarks>
/// Call AFTER
/// <see cref="ReportSubstrateServiceCollectionExtensions.AddBlocksReportsSubstrate"/>
/// and AFTER the upstream cluster registrations
/// (<c>AddBlocksLeases</c>, <c>AddBlocksFinancialAr</c>, <c>AddBlocksPeopleFoundation</c>).
/// The PR 7 umbrella <c>AddBlocksReports()</c> chains all cartridge
/// registrations together for hosts that want the full cluster wired
/// in one call.
/// </remarks>
public static class RentRollServiceCollectionExtensions
{
    /// <summary>
    /// Register the Rent Roll v2 cartridge + its <see cref="ICartridgeRegistrar"/>.
    /// </summary>
    public static IServiceCollection AddRentRollCartridge(
        this IServiceCollection services)
    {
        if (services is null) throw new System.ArgumentNullException(nameof(services));

        services.AddSingleton<RentRollCartridge>();

        services.AddSingleton<IReportCartridge<RentRollParameters, RentRollResult>>(
            sp => sp.GetRequiredService<RentRollCartridge>());

        services.AddSingleton<ICartridgeRegistrar>(sp =>
            new CartridgeRegistrar<RentRollParameters, RentRollResult>(
                sp.GetRequiredService<RentRollCartridge>()));

        return services;
    }
}
