using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.Blocks.AccessGrant;

/// <summary>Reads current authorization by joining active grants to effective definition bindings.</summary>
public sealed class DefinitionJoinedAuthorizationReader(IGrantStore grants, IAuthorizationDefinitionReader definitions)
    : IAuthorizationClosureReader, IAuthorizationClosureSnapshotReader
{
    public async ValueTask<AuthorizationClosureSnapshot> ReadAsync(
        AuthorizationGateRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var derivations = new List<AuthorizationAtomDerivation>();
        var excluded = new List<AuthorizationExcludedBinding>();
        foreach (var versionedGrant in await grants.FindVersionedByPrincipalAsync(
                     request.Tenant, request.Principal, ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            var grant = versionedGrant.Grant;
            var current = await grants.FindVersionedAsync(
                request.Tenant, grant.GrantId, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (current is null || current.OwnerVersion != versionedGrant.OwnerVersion) continue;
            // Ticket 212 slice 2: an inactive grant is still RECORDED, with the reason it is inactive. The
            // gate never sees these; only the counterfactual does, and a lapse would otherwise be an absence.
            AuthorizationExclusionReason? reason = grant.IsActiveAt(request.At) ? null : Exclusion(grant, request.At);
            foreach (var definition in await definitions.DefinitionsForRoleAsync(
                         request.Tenant, grant.Role, ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                var scope = definition.Atom.Scope.Intersect(grant.Scope);
                if (scope is null || !scope.Contains(request.Target.Scope)) continue;
                var derivation = new AuthorizationAtomDerivation(
                    new PermissionAtom(definition.Operation, scope),
                    grant.Role,
                    grant.GrantId.ToString(),
                    versionedGrant.OwnerVersion,
                    definition.DefinitionId.Value.ToString(),
                    grant.Scope,
                    grant.Validity.ValidFrom,
                    grant.Validity.ValidTo,
                    // The in-force fact this reader itself computed for this binding: the IsActiveAt guard
                    // above. Excluded bindings carry false; nothing downstream re-derives the window.
                    InForce: reason is null);
                if (reason is { } excludedFor)
                    excluded.Add(new AuthorizationExcludedBinding(derivation, excludedFor));
                else
                    derivations.Add(derivation);
            }
        }

        return new AuthorizationClosureSnapshot(derivations, excluded);
    }

    /// <summary>Why an inactive grant is inactive — the three arms of <see cref="AccessGrant.IsActiveAt"/>.</summary>
    private static AuthorizationExclusionReason Exclusion(AccessGrant grant, DateTimeOffset at) =>
        grant.Revocation is not null && at >= grant.Revocation.RevokedAt
            ? AuthorizationExclusionReason.GrantRevoked
            : at < grant.GrantedAt || at < grant.Validity.ValidFrom
                ? AuthorizationExclusionReason.NotYetValid
                : AuthorizationExclusionReason.ValidityLapsed;

    public async ValueTask<PermissionAtomSet> UserPermissionsAsync(
        TenantId tenantId, ActorId principal, DateTimeOffset at, CancellationToken ct = default)
    {
        var result = PermissionAtomSet.Empty;
        foreach (var grant in await grants.FindByPrincipalAsync(tenantId, principal, ct).ConfigureAwait(false))
        {
            if (!grant.IsActiveAt(at)) continue;
            foreach (var definition in await definitions.DefinitionsForRoleAsync(tenantId, grant.Role, ct).ConfigureAwait(false))
            {
                var scope = definition.Atom.Scope.Intersect(grant.Scope);
                if (scope is not null)
                    result = result.Union(PermissionAtomSet.Of(new PermissionAtom(definition.Operation, scope)));
            }
        }
        return result;
    }

    public async ValueTask<IReadOnlyList<ActorId>> AssignedUsersAsync(
        TenantId tenantId, PermissionAtom required, DateTimeOffset at, CancellationToken ct = default)
    {
        var subjects = (await grants.SnapshotAsync(tenantId, ct).ConfigureAwait(false))
            .Select(grant => grant.Subject).Distinct().ToArray();
        var assigned = new List<ActorId>();
        foreach (var subject in subjects)
            if ((await UserPermissionsAsync(tenantId, subject, at, ct).ConfigureAwait(false)).Covers(required))
                assigned.Add(subject);
        return assigned;
    }

    public async ValueTask<PermissionAtomSet> RolePermissionsAsync(
        TenantId tenantId, RoleReference role, CancellationToken ct = default)
    {
        var definitionsForRole = await definitions.DefinitionsForRoleAsync(tenantId, role, ct).ConfigureAwait(false);
        return PermissionAtomSet.From(definitionsForRole.Select(definition => definition.Atom));
    }
}
