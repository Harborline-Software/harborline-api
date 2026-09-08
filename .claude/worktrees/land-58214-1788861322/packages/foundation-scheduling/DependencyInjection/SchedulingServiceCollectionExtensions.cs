using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Harborline.Api.Foundation.Scheduling.DependencyInjection;

/// <summary>
/// DI registration for <c>foundation-scheduling</c>.
/// </summary>
public static class SchedulingServiceCollectionExtensions
{
    /// <summary>
    /// Register the RRULE expansion and derived due-queue services. Idempotent via
    /// <see cref="ServiceCollectionDescriptorExtensions.TryAddSingleton{TService, TImplementation}(IServiceCollection)"/>
    /// — re-invocation does not double-register.
    /// </summary>
    public static IServiceCollection AddFoundationScheduling(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IRruleExpansionService, InMemoryRruleExpansionService>();
        services.TryAddSingleton<IDueQueueQueryService, RruleDueQueueQueryService>();
        return services;
    }
}
