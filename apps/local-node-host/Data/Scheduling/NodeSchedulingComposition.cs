using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Harborline.Api.LocalNodeHost.Data.Scheduling;

public static class NodeSchedulingComposition
{
    public static IServiceCollection AddNodeSchedulingAuthoring(this IServiceCollection services)
    {
        services.AddSingleton<NodeSchedulingDraftStore>();
        services.AddSingleton<SchedulingDraftValidator>();
        return services;
    }
}
