using Harborline.Foundation.ContentStore;
using Harborline.Foundation.ExecutionRuntime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Harborline.Api.LocalNodeHost.Data.ContentStore;

/// <summary>Registers the durable storage and runtime quota ports without inventing unmeasured store limits.</summary>
public static class NodeContentStoreComposition
{
    /// <summary>Registers the ports over the node's database and its data directory. A <c>content.db</c> an earlier build left there is ignored; it holds no tenant references (ADR 0113 decision 4).</summary>
    /// <remarks>
    /// With <paramref name="configuration"/>, it also binds the epoch-key rotation settings
    /// (<see cref="ContentKeyRotationSettings.Section"/>) and refuses an out-of-range value here, so the node does not start.
    /// </remarks>
    public static IServiceCollection AddNodeContentStore(this IServiceCollection services, string dataDirectory, IConfiguration? configuration = null)
    {
        services.AddSingleton(configuration is null ? new ContentKeyRotationSettings() : ContentKeyRotationSettings.Bind(configuration));
        services.AddSingleton(_ =>
        {
            var registry = new RunKindRegistry();
            ContentStoreRunRegistration.Register(registry);
            NodeContentKeyRotation.Register(registry);
            return registry;
        });
        services.AddSingleton(provider => new NodeContentStoreStorage(provider.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>(), dataDirectory));
        services.AddSingleton<IContentStoreStorage>(provider => provider.GetRequiredService<NodeContentStoreStorage>());
        services.AddSingleton<IContentStoreQuotaBase, NodeContentStoreQuotaBase>();
        services.AddSingleton(provider => new NodeContentKeyRotation(provider.GetRequiredService<NodeContentStoreStorage>(),
            provider.GetRequiredService<ContentKeyRotationSettings>(), provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<ITenantContentKeyProvider>(provider =>
            new NodeTenantContentKeyProvider(provider.GetRequiredService<Harborline.Api.Foundation.Recovery.TenantKey.IVersionedTenantKeyProvider>()));
        return services;
    }
}
