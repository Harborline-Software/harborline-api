using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.CapabilityAdmission.Authorization;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.Blocks.AccessGrant;

public sealed record AuthorizationDefinitionBindingView(
    AuthorizationCapabilityDefinition Definition,
    long BindingRevision,
    RoleBindingSet EffectiveRoles,
    BindingWarningCode? Warning);

public interface IAuthorizationDefinitionCatalogueReader
{
    ValueTask<IReadOnlyList<AuthorizationDefinitionBindingView>> ListAsync(
        TenantId tenantId, CancellationToken ct = default);

    ValueTask<AuthorizationDefinitionBindingView?> FindAsync(
        TenantId tenantId,
        AuthorizationCapabilityDefinitionId definitionId,
        CancellationToken ct = default);
}
