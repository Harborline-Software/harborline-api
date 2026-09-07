using Microsoft.Extensions.DependencyInjection;
using Harborline.Api.Blocks.Properties.Data;
using Harborline.Api.Blocks.Properties.Services;
using Harborline.Api.Foundation.Persistence;

namespace Harborline.Api.Blocks.Properties.DependencyInjection;

/// <summary>
/// DI extension methods for registering Harborline properties services.
/// </summary>
public static class PropertiesServiceCollectionExtensions
{
    /// <summary>
    /// Registers the in-memory properties surface:
    /// <list type="bullet">
    ///   <item><see cref="IPropertyRepository"/> → <see cref="InMemoryPropertyRepository"/></item>
    ///   <item><see cref="IPropertyUnitRepository"/> → <see cref="InMemoryPropertyUnitRepository"/></item>
    ///   <item><see cref="IHarborlineEntityModule"/> → <see cref="PropertiesEntityModule"/></item>
    /// </list>
    /// Suitable for testing, prototyping, and kitchen-sink demos. Replace
    /// with a persistence-backed <see cref="IPropertyRepository"/> /
    /// <see cref="IPropertyUnitRepository"/> in production hosts.
    /// </summary>
    /// <param name="services">The service collection to register into.</param>
    /// <returns>The same <paramref name="services"/> for fluent chaining.</returns>
    public static IServiceCollection AddInMemoryProperties(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IPropertyRepository, InMemoryPropertyRepository>();
        services.AddSingleton<IPropertyUnitRepository, InMemoryPropertyUnitRepository>();
        services.AddSingleton<IHarborlineEntityModule, PropertiesEntityModule>();

        return services;
    }
}
