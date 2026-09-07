using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.Foundation.Authorization;

/// <summary>
/// Reads the current effective definition atoms for a named role without exposing the
/// access-grant package's definition storage model to the foundation authorization gate.
/// </summary>
public interface IAuthorizationDefinitionAtomReader
{
    ValueTask<IReadOnlyList<PermissionAtom>> AtomsForRoleAsync(
        TenantId tenantId,
        RoleReference role,
        CancellationToken ct = default);
}
