using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Harborline.Api.UICore.Conformance;
using Harborline.Api.UICore.Primitives;

namespace Harborline.Api.UIAdapters.Blazor.A11y;

/// <summary>
/// DI registration for Blazor a11y primitives per ADR 0077 §4 + §6 + §7.
/// </summary>
public static class BlazorA11yServiceExtensions
{
    /// <summary>
    /// Registers Blazor adapter implementations for
    /// <see cref="ILiveAnnouncer"/>, <see cref="IFocusTrap"/>, and
    /// <see cref="IConformanceRegistry"/>. Safe to call multiple times
    /// (uses <c>TryAdd*</c> — subsequent calls are no-ops).
    /// </summary>
    public static IServiceCollection AddHarborlineA11y(this IServiceCollection services)
    {
        services.TryAddTransient<ILiveAnnouncer, BlazorLiveAnnouncer>();
        services.TryAddTransient<IFocusTrap, BlazorFocusTrap>();
        services.TryAddSingleton<IConformanceRegistry, DefaultConformanceRegistry>();
        return services;
    }
}
