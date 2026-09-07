using Harborline.Api.Foundation.Configuration;
using Harborline.Api.Foundation.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Harborline.Api.Foundation.Extensions;

public class HarborlineBuilder
{
    public IServiceCollection Services { get; }

    public HarborlineBuilder(IServiceCollection services)
    {
        Services = services;
    }
}

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers Harborline foundation services and applies optional configuration.
    /// Registers <see cref="IHarborlineThemeService"/> and <see cref="IHarborlineNotificationService"/>
    /// automatically. Call on the returned <see cref="HarborlineBuilder"/> to add adapter-specific
    /// services (e.g., Blazor interop) via extension methods in the adapter packages.
    /// </summary>
    public static HarborlineBuilder AddHarborline(this IServiceCollection services, Action<HarborlineOptions>? configure = null)
    {
        var options = new HarborlineOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);
        services.AddScoped<IHarborlineThemeService, HarborlineThemeService>();
        services.AddScoped<IHarborlineNotificationService, HarborlineNotificationService>();
        return new HarborlineBuilder(services);
    }
}
