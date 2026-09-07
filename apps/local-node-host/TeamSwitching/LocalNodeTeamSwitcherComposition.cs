using Harborline.Api.Kernel.Runtime.DependencyInjection;
using Harborline.Api.UIAdapters.Blazor.Components.LocalFirst;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Harborline.Api.LocalNodeHost;

internal static class LocalNodeTeamSwitcherComposition
{
    public static IServiceCollection AddLocalNodeTeamSwitcher(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHarborlineNotificationAggregator();
        services.TryAddSingleton<ITeamSwitcherState, KernelTeamSwitcherState>();
        return services;
    }
}
