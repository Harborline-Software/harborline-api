using System.Security.Cryptography;
using Harborline.Foundation.ContentStore;
using Microsoft.Extensions.DependencyInjection;

namespace Harborline.Api.LocalNodeHost.Data.ContentStore;

/// <summary>Registers the durable storage and runtime quota ports without inventing unmeasured store limits.</summary>
public static class NodeContentStoreComposition
{
    public static IServiceCollection AddNodeContentStore(this IServiceCollection services, string dataDirectory, ReadOnlySpan<byte> atRestRootKey)
    {
        if (atRestRootKey.Length != 32) throw new ArgumentException("A 256-bit at-rest root key is required.", nameof(atRestRootKey));
        // A separate database key purpose, derived from the installation's recoverable at-rest root.
        var key = new byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, atRestRootKey, key, salt: [], info: "harborline-content-store:database:v1"u8);
        var directory = Path.Combine(dataDirectory, "content-store");
        // The container builds and disposes the storage, so a composition abandoned before its provider is built
        // never holds the writer lease. The key is cleared once the singleton opens; a failed open keeps it so a
        // retry opens the same database rather than creating one under a cleared key.
        services.AddSingleton(_ =>
        {
            var storage = new NodeContentStoreStorage(directory, key);
            CryptographicOperations.ZeroMemory(key);
            return storage;
        });
        services.AddSingleton<IContentStoreStorage>(provider => provider.GetRequiredService<NodeContentStoreStorage>());
        services.AddSingleton<IContentStoreQuotaBase, NodeContentStoreQuotaBase>();
        return services;
    }
}
