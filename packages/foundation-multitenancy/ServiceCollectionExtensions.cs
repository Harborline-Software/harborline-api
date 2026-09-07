using Microsoft.Extensions.DependencyInjection;

namespace Harborline.Api.Foundation.MultiTenancy;

/// <summary>DI conveniences for <see cref="Harborline.Api.Foundation.MultiTenancy"/>.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="InMemoryTenantCatalog"/> as a singleton and exposes it
    /// as <see cref="ITenantCatalog"/>, <see cref="ITenantResolver"/>, and
    /// <see cref="ITenantLifecycleService"/> — one backing store behind all three.
    /// </summary>
    public static IServiceCollection AddHarborlineTenantCatalog(this IServiceCollection services)
    {
        services.AddSingleton<InMemoryTenantCatalog>();
        services.AddSingleton<ITenantCatalog>(sp => sp.GetRequiredService<InMemoryTenantCatalog>());
        services.AddSingleton<ITenantResolver>(sp => sp.GetRequiredService<InMemoryTenantCatalog>());
        services.AddSingleton<ITenantLifecycleService>(sp => sp.GetRequiredService<InMemoryTenantCatalog>());
        return services;
    }
}
