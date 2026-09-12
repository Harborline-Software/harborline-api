using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Install;

namespace Harborline.Api.LocalNodeHost.Data.PackProjection;

/// <summary>Rejects a pack item that attempts to author a compiled system record type.</summary>
public static class PackSealedSystemTypeAdmission
{
    /// <summary>The stable, localizable reason reported for a claimed compiled type.</summary>
    public const string RefusedCode = "pack.projection.sealed_system_type_claim";

    /// <summary>
    /// System record-type names are the closed pack-kind vocabulary. They belong to the platform
    /// catalogue and are compiled, so a carried item may not claim one as its content key.
    /// </summary>
    public static bool ClaimsSealedSystemType(PackSeedItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return Enum.GetNames<PackContentKind>()
            .Contains(item.Key, StringComparer.OrdinalIgnoreCase);
    }
}
