using Microsoft.Extensions.DependencyInjection;
using Harborline.Api.Blocks.Reports.Cartridges.ProfitAndLossByProperty;

namespace Harborline.Api.Blocks.Reports.DependencyInjection;

/// <summary>
/// DI extension for the P&amp;L by Property cartridge.
/// W#72 PR 5 per Stage 02 §4.15.
/// </summary>
/// <remarks>
/// Call AFTER
/// <see cref="ReportSubstrateServiceCollectionExtensions.AddBlocksReportsSubstrate"/>
/// and AFTER the upstream cluster registration that provides
/// <c>IJournalStore</c> and <c>IAccountResolver</c>
/// (both ship with <c>blocks-financial-ledger</c>).
/// The PR 7 umbrella <c>AddBlocksReports()</c> chains all cartridge
/// registrations together for hosts that want the full cluster wired
/// in one call.
/// </remarks>
public static class ProfitAndLossByPropertyServiceCollectionExtensions
{
    /// <summary>
    /// Register the P&amp;L by Property cartridge + its
    /// <see cref="ICartridgeRegistrar"/>.
    /// </summary>
    public static IServiceCollection AddProfitAndLossByPropertyCartridge(
        this IServiceCollection services)
    {
        if (services is null) throw new System.ArgumentNullException(nameof(services));

        services.AddSingleton<ProfitAndLossByPropertyCartridge>();

        services.AddSingleton<IReportCartridge<ProfitAndLossByPropertyParameters, ProfitAndLossByPropertyResult>>(
            sp => sp.GetRequiredService<ProfitAndLossByPropertyCartridge>());

        services.AddSingleton<ICartridgeRegistrar>(sp =>
            new CartridgeRegistrar<ProfitAndLossByPropertyParameters, ProfitAndLossByPropertyResult>(
                sp.GetRequiredService<ProfitAndLossByPropertyCartridge>()));

        return services;
    }
}
