using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.LocalNodeHost.Data.Search;
using Microsoft.Data.Sqlite;

namespace Harborline.Api.LocalNodeHost.Tests.Search;

internal static class TestSearchAuthorization
{
    internal static readonly RoleReference RecordsReader = AccessGrantAuthorizationSeed.MemberRole;

    public static AccessGrant Grant(TenantId tenant, ActorId subject, ScopeExpression scope,
        DateTimeOffset now, GrantResidency residency = GrantResidency.Cache, bool canRead = true,
        DateTimeOffset? validTo = null, GrantRevocation? revocation = null) => new(
        GrantId.New(), tenant, subject, canRead ? RecordsReader : RoleReference.Auditor,
        scope, residency, new GrantValidity(
            validTo is { } end && end <= now.AddDays(-1) ? end.AddDays(-1) : now.AddDays(-1),
            validTo), GranterKind.Person,
        new ActorId("owner"), now.AddDays(-1),
        new GrantProvenance(GrantSourceKind.Manual, new GrantReason(GrantReasonCodes.Manual), new ActorId("owner")),
        now.AddDays(-1), revocation is null ? GrantStatus.Active : GrantStatus.Revoked, revocation);

    public static IAuthorizationClosureReader Reader(IGrantStore grants) =>
        new DefinitionJoinedAuthorizationReader(grants, new RecordsReadDefinitions());

    public static IAuthorizedRecordSetProjection Projection(IGrantStore grants) =>
        new LiveJoinTestProjection(grants, Reader(grants));

    private sealed class LiveJoinTestProjection(
        IGrantStore grants,
        IAuthorizationClosureReader authorization) : IAuthorizedRecordSetProjection
    {
        public async Task<AuthorizedRecordScope> ResolveAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            TenantId tenantId,
            ActorId principalId,
            DateTimeOffset at,
            CancellationToken ct = default)
        {
            _ = connection;
            _ = transaction;
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var grant in await grants.FindByPrincipalAsync(tenantId, principalId, ct))
            {
                if (!grant.IsActiveAt(at) || grant.Residency != GrantResidency.Cache) continue;
                var rolePermissions = await authorization.RolePermissionsAsync(tenantId, grant.Role, ct);
                foreach (var atom in rolePermissions.Atoms.Where(atom => atom.Operation.Value == "records:read"))
                {
                    var scope = atom.Scope.Intersect(grant.Scope);
                    if (scope?.Value == "/") return AuthorizedRecordScope.EntireTenant;
                    const string prefix = "/records/";
                    if (scope is null || !scope.Value.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    var id = scope.Value[prefix.Length..];
                    if (id.Length > 0 && !id.Contains('/', StringComparison.Ordinal)) ids.Add(id);
                }
            }
            return AuthorizedRecordScope.ForRecordIds(ids);
        }
    }

    private sealed class RecordsReadDefinitions : IAuthorizationDefinitionReader
    {
        private static readonly PermissionAtom Atom = PermissionAtom.Parse("records:read@/");
        public ValueTask<IReadOnlyList<AuthorizationCapabilityDefinition>> DefinitionsForRoleAsync(
            TenantId tenantId, RoleReference role, CancellationToken ct = default) =>
            ValueTask.FromResult<IReadOnlyList<AuthorizationCapabilityDefinition>>(role == RecordsReader
                ? [new(new AuthorizationCapabilityDefinitionId(Guid.Parse("10000000-0000-0000-0000-000000000001")),
                    "test", 1, Atom.Operation, Atom, RoleBindingSet.From([RecordsReader]))]
                : []);
        public ValueTask<RoleBindingSet> EffectiveBindingAsync(
            TenantId tenantId, AuthorizationCapabilityDefinitionId definitionId, CancellationToken ct = default) =>
            ValueTask.FromResult(RoleBindingSet.From([RecordsReader]));
    }
}

internal static class AuthorizedRecordProjectionTestExtensions
{
    public static async Task<AuthorizedRecordScope> ResolveAsync(
        this IAuthorizedRecordSetProjection projection,
        TenantId tenantId,
        ActorId principalId,
        DateTimeOffset at,
        CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        return await projection.ResolveAsync(connection, transaction, tenantId, principalId, at, ct);
    }
}
