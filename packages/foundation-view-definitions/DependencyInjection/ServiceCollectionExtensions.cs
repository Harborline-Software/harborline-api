using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Harborline.Api.Foundation.ViewDefinitions.DependencyInjection;

/// <summary>
/// DI registration for the foundation-tier view-definition substrate (control ticket 074).
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the in-memory <see cref="IViewDefinitionRegistry"/> reference implementation and
    /// the pass-through <see cref="IViewDefinitionCanonicalizer"/>. The
    /// <see cref="IViewDefinitionDescriptorRegistry"/> is deliberately NOT registered here: the
    /// hosting composition supplies it, because descriptor admission (typed parameter binding,
    /// tenant-reference validation) is host knowledge, and runtime view-surface registration must
    /// not become the wire-validity authority.
    /// </summary>
    public static IServiceCollection AddInMemoryViewDefinitions(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IViewDefinitionCanonicalizer, PassThroughViewDefinitionCanonicalizer>();
        services.TryAddSingleton<InMemoryViewDefinitionRegistry>();
        services.TryAddSingleton<IViewDefinitionRegistry>(sp => sp.GetRequiredService<InMemoryViewDefinitionRegistry>());
        return services;
    }
}
