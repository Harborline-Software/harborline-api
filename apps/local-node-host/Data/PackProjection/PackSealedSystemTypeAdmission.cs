using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.LocalNodeHost.Health;

namespace Harborline.Api.LocalNodeHost.Data.PackProjection;

/// <summary>Rejects a pack item that attempts to author a compiled system record type.</summary>
public static class PackSealedSystemTypeAdmission
{
    /// <summary>The sole package permitted to catalogue the compiled platform type descriptors.</summary>
    public const string PlatformPackKey = "harborline.platform";
    /// <summary>The stable, localizable reason reported for a claimed compiled type.</summary>
    public const string RefusedCode = "pack.projection.sealed_system_type_claim";

    /// <summary>
    /// System record-type names are the closed pack-kind vocabulary. They belong to the platform
    /// catalogue and are compiled, so a carried item may not claim one as its content key.
    /// </summary>
    public static bool ClaimsSealedSystemType(PackSeedItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return TryResolveCompiledDescriptor(item.Key, out _);
    }

    /// <summary>
    /// Resolves a carried platform record-type key to the one compiled catalogue descriptor.
    /// The returned instance is taken directly from <see cref="SystemRecordType.All"/>; package
    /// content never materializes a second descriptor.
    /// </summary>
    public static bool TryResolveCompiledDescriptor(string key, out SystemRecordType? descriptor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        descriptor = SystemRecordType.All.SingleOrDefault(type =>
            string.Equals(type.Name, key, StringComparison.OrdinalIgnoreCase));
        return descriptor is not null;
    }

    /// <summary>
    /// The platform seed records the compiled descriptors for catalogue provenance, but never supplies
    /// a second schema. Every other package claiming one remains refused.
    /// </summary>
    public static bool IsPermittedPlatformCatalogueItem(string packKey, PackSeedItem item)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packKey);
        return string.Equals(packKey, PlatformPackKey, StringComparison.Ordinal)
               && ClaimsSealedSystemType(item);
    }
}
