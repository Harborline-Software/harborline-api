using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Harborline.Api.Foundation.ScheduleDefinitions.DependencyInjection;

/// <summary>
/// DI registration for the foundation-tier schedule-definition substrate (control ticket 075).
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the in-memory <see cref="IScheduleDefinitionRegistry"/> reference implementation and
    /// the pass-through <see cref="IScheduleDefinitionCanonicalizer"/>. The
    /// <see cref="IScheduleDefinitionDescriptorRegistry"/> is deliberately NOT registered here: the
    /// hosting composition supplies it, because admission against the existing scheduling authoring
    /// contract is host knowledge.
    /// </summary>
    public static IServiceCollection AddInMemoryScheduleDefinitions(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IScheduleDefinitionCanonicalizer, PassThroughScheduleDefinitionCanonicalizer>();
        services.TryAddSingleton<InMemoryScheduleDefinitionRegistry>();
        services.TryAddSingleton<IScheduleDefinitionRegistry>(
            sp => sp.GetRequiredService<InMemoryScheduleDefinitionRegistry>());
        return services;
    }
}
