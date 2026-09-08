using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

public sealed class DefinitionJoinedAuthorizationReaderTests
{
    private static readonly TenantId Tenant = TenantId.FromString("tenant-a");
    private static readonly ActorId User = new("user-a");
    private static readonly RoleReference Member = AccessGrantAuthorizationSeed.MemberRole;
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-01T12:00:00Z");

    [Fact]
    public async Task DefinitionJoinedReader_UnionsEveryActiveGrant()
    {
        var grants = TestInMemoryAuthorizationStores.GrantStore();
        await grants.AppendAsync(Tenant, Grant(ScopeExpression.Parse("/north")), "north");
        await grants.AppendAsync(Tenant, Grant(ScopeExpression.Parse("/south")), "south");
        var reader = new DefinitionJoinedAuthorizationReader(grants, new Definitions(Member, PermissionAtom.Parse("records:read@/")));
        var result = await reader.UserPermissionsAsync(Tenant, User, Now);
        Assert.True(result.Covers(PermissionAtom.Parse("records:read@/north/a")));
        Assert.True(result.Covers(PermissionAtom.Parse("records:read@/south/a")));
    }

    [Fact]
    public async Task DefinitionJoinedReader_UnreferencedRoleReturnsEmpty()
    {
        var grants = TestInMemoryAuthorizationStores.GrantStore();
        await grants.AppendAsync(Tenant, Grant(ScopeExpression.Parse("/")), "grant");
        var reader = new DefinitionJoinedAuthorizationReader(grants, new Definitions(RoleReference.Auditor, PermissionAtom.Parse("records:read@/")));
        Assert.Equal(PermissionAtomSet.Empty, await reader.UserPermissionsAsync(Tenant, User, Now));
    }

    [Fact]
    public async Task DefinitionJoinedReader_DisjointScopeReturnsEmpty()
    {
        var grants = TestInMemoryAuthorizationStores.GrantStore();
        await grants.AppendAsync(Tenant, Grant(ScopeExpression.Parse("/south")), "grant");
        var reader = new DefinitionJoinedAuthorizationReader(grants, new Definitions(Member, PermissionAtom.Parse("records:read@/north")));
        Assert.Equal(PermissionAtomSet.Empty, await reader.UserPermissionsAsync(Tenant, User, Now));
    }

    [Fact]
    public async Task DefinitionJoinedReader_RevokedGrantReturnsEmpty()
    {
        var grants = TestInMemoryAuthorizationStores.GrantStore();
        var grant = await grants.AppendAsync(Tenant, Grant(ScopeExpression.Parse("/")), "grant");
        await grants.RevokeAsync(Tenant, grant.GrantId, new GrantRevocation(
            new ActorId("admin"), Now, new GrantReason(GrantReasonCodes.RevocationOffboarding)));
        var reader = new DefinitionJoinedAuthorizationReader(grants, new Definitions(Member, PermissionAtom.Parse("records:read@/")));
        Assert.Equal(PermissionAtomSet.Empty, await reader.UserPermissionsAsync(Tenant, User, Now));
    }

    private static AccessGrant Grant(ScopeExpression scope) => new(
        GrantId.New(), Tenant, User, Member, scope, GrantResidency.Cache,
        new GrantValidity(Now.AddHours(-1), Now.AddHours(1)), GranterKind.Person,
        new ActorId("admin"), Now.AddHours(-1),
        new GrantProvenance(GrantSourceKind.Manual, new GrantReason(GrantReasonCodes.Manual), new ActorId("admin")),
        Now.AddHours(-1));

    private sealed class Definitions(RoleReference role, PermissionAtom atom) : IAuthorizationDefinitionReader
    {
        public ValueTask<IReadOnlyList<AuthorizationCapabilityDefinition>> DefinitionsForRoleAsync(
            TenantId tenantId, RoleReference requested, CancellationToken ct = default) =>
            ValueTask.FromResult<IReadOnlyList<AuthorizationCapabilityDefinition>>(requested == role
                ? [new(new AuthorizationCapabilityDefinitionId(Guid.NewGuid()), "test", 1,
                    atom.Operation, atom, RoleBindingSet.From([role]))]
                : []);

        public ValueTask<RoleBindingSet> EffectiveBindingAsync(
            TenantId tenantId, AuthorizationCapabilityDefinitionId definitionId, CancellationToken ct = default) =>
            ValueTask.FromResult(RoleBindingSet.From([role]));
    }
}
