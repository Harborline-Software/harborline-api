using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

internal sealed class FixedAuthorizationClosure : IAuthorizationClosureReader
{
    private static readonly PermissionAtomSet Required = PermissionAtomSet.Of(
        PermissionAtom.Parse("members:manage@/"));
    private readonly PermissionAtomSet _user;
    private readonly PermissionAtomSet _role;

    public FixedAuthorizationClosure(bool authorized = true)
        : this(authorized ? Required : PermissionAtomSet.Empty, Required) { }

    public FixedAuthorizationClosure(PermissionAtomSet user, PermissionAtomSet? role = null)
    {
        _user = user;
        _role = role ?? user;
    }

    public ValueTask<PermissionAtomSet> UserPermissionsAsync(
        TenantId tenantId, ActorId principal, DateTimeOffset at, CancellationToken ct = default) =>
        ValueTask.FromResult(_user);

    public ValueTask<IReadOnlyList<ActorId>> AssignedUsersAsync(
        TenantId tenantId, PermissionAtom required, DateTimeOffset at, CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<ActorId>>(Array.Empty<ActorId>());

    public ValueTask<PermissionAtomSet> RolePermissionsAsync(
        TenantId tenantId, RoleReference role, CancellationToken ct = default) =>
        ValueTask.FromResult(_role);
}

internal sealed class LegacyPermissionAuthorizationClosure(
    IReadOnlyDictionary<string, PermissionSet> permissions) : IAuthorizationClosureReader
{
    public ValueTask<PermissionAtomSet> UserPermissionsAsync(
        TenantId tenantId, ActorId principal, DateTimeOffset at, CancellationToken ct = default) =>
        ValueTask.FromResult(permissions.TryGetValue(principal.Value, out var held)
            ? PermissionAtomSet.From(held.Permissions.Select(operation => PermissionAtom.Parse($"{operation}@/")))
            : PermissionAtomSet.Empty);

    public ValueTask<IReadOnlyList<ActorId>> AssignedUsersAsync(
        TenantId tenantId, PermissionAtom required, DateTimeOffset at, CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<ActorId>>(Array.Empty<ActorId>());

    public ValueTask<PermissionAtomSet> RolePermissionsAsync(
        TenantId tenantId, RoleReference role, CancellationToken ct = default) =>
        ValueTask.FromResult(PermissionAtomSet.Empty);
}
