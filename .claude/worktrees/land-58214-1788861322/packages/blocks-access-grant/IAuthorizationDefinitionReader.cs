using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.Blocks.AccessGrant;

/// <summary>Definition-led authorization binding queries.</summary>
public interface IAuthorizationDefinitionReader : IAuthorizationDefinitionAtomReader
{
    /// <summary>Returns current definitions whose effective tenant binding names the role.</summary>
    ValueTask<IReadOnlyList<AuthorizationCapabilityDefinition>> DefinitionsForRoleAsync(
        TenantId tenantId,
        RoleReference role,
        CancellationToken ct = default);

    /// <summary>Returns the current publisher/tenant intersection for a definition.</summary>
    ValueTask<RoleBindingSet> EffectiveBindingAsync(
        TenantId tenantId,
        AuthorizationCapabilityDefinitionId definitionId,
        CancellationToken ct = default);

    async ValueTask<IReadOnlyList<PermissionAtom>> IAuthorizationDefinitionAtomReader.AtomsForRoleAsync(
        TenantId tenantId,
        RoleReference role,
        CancellationToken ct)
    {
        var definitions = await DefinitionsForRoleAsync(tenantId, role, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return definitions.Select(definition => definition.Atom).ToArray();
    }
}
