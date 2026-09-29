using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Trust;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

/// <summary>
/// Seeds an empty Active <c>harborline.platform</c> pack into a bare store, as the host's platform preload does
/// at boot. DES-0029 ck-2 S9 (D5) roots every closure at it, so a fixture that activates packs over a bare
/// store calls this first. A no-op when the platform pack is already Active for the tenant.
/// </summary>
internal static class PlatformPackTestPreload
{
    internal const string PackKey = "harborline.platform";
    internal const string Version = "1.0.0";

    internal static void Activate(IPackInstallMutationStore store, TenantId tenant)
    {
        if (store.GetActive(tenant, PackKey) is not null) return;
        store.Commit(new PackInstallTransaction(tenant,
            new InstalledPack(PackKey, Version, PackScopeTier.Horizontal, PackLifecycleState.Draft, [],
                new Dictionary<string, int>(), DateTimeOffset.UnixEpoch,
                PrincipalId.FromBytes(new byte[PrincipalId.LengthInBytes]), 1, TrustScope.OwnRoster, []),
            new PackInstallWatermark(PackKey, Version, new Dictionary<string, int>()), []));
        store.Activate(tenant, PackKey, Version);
    }
}
