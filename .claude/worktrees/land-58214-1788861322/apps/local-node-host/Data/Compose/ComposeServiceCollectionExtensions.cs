using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Harborline.Api.LocalNodeHost.Data.Compose;

/// <summary>
/// Registers the Pack Composer B-2a compose ceremony (the snapshot / affirm / guarded-export engine). The
/// pack export/verify + DCP services (<c>AddPackComposerExportVerify</c>) and the asset registry
/// (<c>AddNodeAssetRegistry</c>) are the ceremony's collaborators — call this AFTER both.
/// </summary>
public static class ComposeServiceCollectionExtensions
{
    /// <summary>Adds the in-memory draft-composition store + the compose ceremony (both node-lifetime).</summary>
    public static IServiceCollection AddPackComposeCeremony(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IDraftCompositionStore, InMemoryDraftCompositionStore>();
        services.TryAddSingleton<ComposeCeremony>();
        return services;
    }
}
