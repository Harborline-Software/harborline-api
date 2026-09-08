using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.Blocks.AccessGrant;

/// <summary>
/// Compatibility projection of the authorization closure. Domain writers use
/// <c>IAuthorizationClosureSnapshotReader</c> through the kernel authorization gate.
/// </summary>
public interface IAuthorizationClosureReader
{
    ValueTask<PermissionAtomSet> UserPermissionsAsync(TenantId tenantId, ActorId principal, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<IReadOnlyList<ActorId>> AssignedUsersAsync(TenantId tenantId, PermissionAtom required, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<PermissionAtomSet> RolePermissionsAsync(TenantId tenantId, RoleReference role, CancellationToken ct = default);
}
