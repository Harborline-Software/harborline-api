using Harborline.Foundation.ContentStore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Harborline.Api.LocalNodeHost.Data.ContentStore;

/// <summary>Registers the durable storage and runtime quota ports without inventing unmeasured store limits.</summary>
public static class NodeContentStoreComposition
{
    /// <summary>Registers the ports over the node's database and its data directory. A <c>content.db</c> an earlier build left there is ignored; it holds no tenant references (ADR 0113 decision 4).</summary>
    public static IServiceCollection AddNodeContentStore(this IServiceCollection services, string dataDirectory)
    {
        services.AddSingleton(provider => new NodeContentStoreStorage(provider.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>(), dataDirectory));
        services.AddSingleton<IContentStoreStorage>(provider => provider.GetRequiredService<NodeContentStoreStorage>());
        services.AddSingleton<IContentStoreQuotaBase, NodeContentStoreQuotaBase>();
        return services;
    }
}
