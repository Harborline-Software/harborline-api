using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Packs.Install;

namespace Harborline.Api.LocalNodeHost.Data.Packs;

/// <summary>Read-only facade over the durable pack state; it deliberately exposes no mutation face.</summary>
internal sealed class DurablePackInstallStoreReader(DurablePackInstallStore inner) : IPackInstallStore
{
    public InstalledPack? GetActive(TenantId tenant, string packKey) => inner.GetActive(tenant, packKey);

    public InstalledPack? GetVersion(TenantId tenant, string packKey, string version) =>
        inner.GetVersion(tenant, packKey, version);

    public IReadOnlyList<InstalledPack> ListInstalled(TenantId tenant) => inner.ListInstalled(tenant);

    public PackInstallWatermark? GetWatermark(TenantId tenant, string packKey) =>
        inner.GetWatermark(tenant, packKey);

    public IReadOnlyList<PackTenantOverride> GetOverrides(TenantId tenant, string packKey) =>
        inner.GetOverrides(tenant, packKey);

    public IReadOnlyDictionary<string, string> GetKeyOwnership(TenantId tenant) =>
        inner.GetKeyOwnership(tenant);
}
