using Harborline.Api.Foundation.Recovery.TenantKey;
using Harborline.Foundation.ContentStore;
using Harborline.Foundation.Assets.Common;

namespace Harborline.Api.LocalNodeHost.Data.ContentStore;

/// <summary>
/// The content store's tenant key on the node: the tenant's version-2 stored key, never a version-1 root-derived one
/// (ADR 0116 decision 4, ADR 0012). A stored key can be destroyed, so destroying the tenant's keys destroys every epoch
/// key wrapped by its data-key-wrap subkey and erases the tenant's content; a derived key cannot be destroyed.
/// </summary>
/// <remarks>
/// The node holds no content-store rows under a version-1 key: the store had no production composition before its epoch
/// layer, and host enablement stays held, so there is nothing to move to a version-2 key before an epoch is made.
/// </remarks>
public sealed class NodeTenantContentKeyProvider(IVersionedTenantKeyProvider keys) : ITenantContentKeyProvider
{
    /// <summary>The purpose the content store's tenant key is stored under.</summary>
    public const string Purpose = "content-store";

    /// <summary>The stored-key version the content store reads: the destroyable version-2 hierarchy.</summary>
    public const int KeyVersion = 2;

    /// <inheritdoc />
    public byte[] GetKey(TenantId tenantId) =>
        keys.ResolveKey(tenantId, Purpose, KeyVersion, CancellationToken.None).ToArray();
}
