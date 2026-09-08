using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.Blocks.AccessGrant;

/// <summary>Reconstructs authority from effective-dated source records at an explicit instant.</summary>
public interface IHistoricalAuthorizationResolver
{
    /// <summary>Returns the scoped permission atoms held by a principal at <paramref name="at"/>.</summary>
    ValueTask<PermissionAtomSet> AuthorityAtAsync(
        TenantId tenantId,
        ActorId principal,
        DateTimeOffset at,
        CancellationToken ct = default);
}

/// <summary>Reads definition and binding revisions effective at an explicit instant.</summary>
public interface IHistoricalAuthorizationConfigurationReader
{
    ValueTask<IReadOnlyList<AuthorizationCapabilityDefinition>> DefinitionsForRoleAtAsync(
        TenantId tenantId,
        RoleReference role,
        DateTimeOffset at,
        CancellationToken ct = default);
}

/// <summary>Joins effective-dated definitions and bindings to grants active at the requested instant.</summary>
public sealed class HistoricalAuthorizationResolver(
    IGrantStore grants,
    IHistoricalAuthorizationConfigurationReader configuration) : IHistoricalAuthorizationResolver
{
    public async ValueTask<PermissionAtomSet> AuthorityAtAsync(
        TenantId tenantId,
        ActorId principal,
        DateTimeOffset at,
        CancellationToken ct = default)
    {
        var result = PermissionAtomSet.Empty;
        foreach (var grant in await grants.FindByPrincipalAsync(tenantId, principal, ct).ConfigureAwait(false))
        {
            if (!grant.IsActiveAt(at)) continue;
            var definitions = await configuration
                .DefinitionsForRoleAtAsync(tenantId, grant.Role, at, ct)
                .ConfigureAwait(false);
            foreach (var definition in definitions)
            {
                var scope = definition.Atom.Scope.Intersect(grant.Scope);
                if (scope is not null)
                    result = result.Union(PermissionAtomSet.Of(new PermissionAtom(definition.Operation, scope)));
            }
        }

        return result;
    }
}
