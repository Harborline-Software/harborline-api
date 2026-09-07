using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Harborline.Api.Foundation.EngineRoom;

/// <summary>Dependency-injection composition for Engine Room telemetry.</summary>
public static class EngineRoomServiceCollectionExtensions
{
    /// <summary>Registers one process-wide Engine Room diagnostics emitter.</summary>
    /// <param name="services">Service collection to update.</param>
    /// <returns>The supplied service collection.</returns>
    public static IServiceCollection AddHarborlineEngineRoom(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<EngineRoomTelemetry>();
        return services;
    }
}
