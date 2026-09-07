using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Harborline.Api.Foundation.DataExchangeDefinitions.DependencyInjection;

/// <summary>
/// DI registration for the foundation-tier data-exchange-definition substrate (control ticket 076).
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the in-memory <see cref="IDataExchangeDefinitionRegistry"/> reference implementation and
    /// the pass-through <see cref="IDataExchangeDefinitionCanonicalizer"/>. The
    /// <see cref="IDataExchangeDefinitionDescriptorRegistry"/> is deliberately NOT registered here: the
    /// hosting composition supplies it, because descriptor admission is host knowledge, and runtime adapter
    /// registration must not become the wire-validity authority.
    /// </summary>
    public static IServiceCollection AddInMemoryDataExchangeDefinitions(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IDataExchangeDefinitionCanonicalizer, PassThroughDataExchangeDefinitionCanonicalizer>();
        services.TryAddSingleton<InMemoryDataExchangeDefinitionRegistry>();
        services.TryAddSingleton<IDataExchangeDefinitionRegistry>(sp => sp.GetRequiredService<InMemoryDataExchangeDefinitionRegistry>());
        return services;
    }
}
