using Harborline.Api.Foundation.Recovery.TenantKey;

using Microsoft.Extensions.DependencyInjection;

namespace Harborline.Api.LocalNodeHost;

/// <summary>Production composition for the stored, destroyable tenant-key hierarchy.</summary>
public static class LocalNodeTenantKeyComposition
{
    /// <summary>Register the durable stored-key provider under the resolved hierarchy root.</summary>
    public static IServiceCollection ConfigureStoredTenantKeys(
        this IServiceCollection services,
        string keyDirectory,
        ReadOnlySpan<byte> hierarchyRoot,
        ITenantKeyProvider? legacyProvider = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyDirectory);
        var root = hierarchyRoot.ToArray();
        var store = new FileSystemTenantKeyStore(keyDirectory);
        var provider = new StoredTenantKeyProvider(store, root, legacyProvider);
        services.AddSingleton<IStoredTenantKeyStore>(store);
        services.AddSingleton(provider);
        services.AddSingleton<ITenantKeyProvider>(provider);
        services.AddSingleton<ITenantKeyDestroyer>(provider);
        return services;
    }
}
