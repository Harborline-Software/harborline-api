using Harborline.Foundation.DataExchange;

using Microsoft.Extensions.DependencyInjection;

namespace Harborline.Api.LocalNodeHost.Data.DataExchange;

/// <summary>Composes the released platform Data Exchange stores at the API host boundary.</summary>
public static class DataExchangePlatformComposition
{
    /// <summary>
    /// Registers operational state owned by the node. The definition/runtime contracts themselves
    /// come from the pinned <c>Harborline.Foundation.DataExchange</c> package.
    /// </summary>
    public static IServiceCollection AddPlatformDataExchange(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IDataExchangeDefinitionStore, InMemoryDataExchangeDefinitionStore>();
        services.AddSingleton<IExchangeRunStore, InMemoryExchangeRunStore>();
        services.AddSingleton<IAcquisitionCheckpointStore, InMemoryAcquisitionCheckpointStore>();
        services.AddSingleton<IProtectedEffectPayloadStore, InMemoryProtectedEffectPayloadStore>();
        return services;
    }
}
