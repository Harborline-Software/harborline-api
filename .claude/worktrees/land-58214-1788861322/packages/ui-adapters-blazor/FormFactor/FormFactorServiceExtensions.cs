using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Harborline.Api.UIAdapters.Blazor.FormFactor;

/// <summary>
/// DI registration for the FF10 Blazor form-factor runtime (task #154 §6).
/// </summary>
public static class FormFactorServiceExtensions
{
    /// <summary>
    /// Registers <see cref="IFormFactorService"/> as scoped (one instance per Blazor
    /// circuit/tab — matches the ui-react runtime's one-hook-tree-per-page-load lifetime). Safe
    /// to call multiple times (uses <c>TryAdd*</c> — subsequent calls are no-ops).
    /// </summary>
    public static IServiceCollection AddHarborlineFormFactor(this IServiceCollection services)
    {
        services.TryAddScoped<IFormFactorService, FormFactorService>();
        return services;
    }
}
