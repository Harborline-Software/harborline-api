using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Harborline.Api.Foundation.ReportDefinitions.DependencyInjection;

/// <summary>
/// DI registration for the foundation-tier report-definition substrate (control ticket 073).
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the in-memory <see cref="IReportDefinitionRegistry"/> reference implementation and
    /// the pass-through <see cref="IReportDefinitionCanonicalizer"/>. The
    /// <see cref="IReportDefinitionDescriptorRegistry"/> is deliberately NOT registered here: the
    /// hosting composition supplies it, because descriptor admission (typed parameter binding,
    /// tenant-reference validation) is host knowledge, and runtime cartridge registration must not
    /// become the wire-validity authority.
    /// </summary>
    public static IServiceCollection AddInMemoryReportDefinitions(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IReportDefinitionCanonicalizer, PassThroughReportDefinitionCanonicalizer>();
        services.TryAddSingleton<InMemoryReportDefinitionRegistry>();
        services.TryAddSingleton<IReportDefinitionRegistry>(sp => sp.GetRequiredService<InMemoryReportDefinitionRegistry>());
        return services;
    }
}
