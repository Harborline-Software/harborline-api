using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.CapabilityAdmission.Authorization;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.LocalNodeHost.Data.Authorization;
using Harborline.Api.LocalNodeHost.Data.Search;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Harborline.Api.LocalNodeHost.Tests.Authorization;

public sealed class AuthorizationAdminTenantVisibilityStoreTests
{
    [Fact]
    public async Task DurableCatalogue_PersistsDeclaringTenantAndHidesItFromOtherTenants()
    {
        var tenantA = new TenantId("aaaaaaaa-0000-0000-0000-000000000001");
        var tenantB = new TenantId("bbbbbbbb-0000-0000-0000-000000000002");
        var tenantRole = RoleDefinition.CreateTenantRole(
            new RoleDefinitionId(Guid.Parse("cccccccc-0000-0000-0000-000000000003")),
            "private-reviewer",
            "Private reviewer",
            tenantA);
        var vocabulary = new InMemoryRoleVocabulary([tenantRole]);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<NodeLocalSearchDbContext>()
            .UseSqlite(connection)
            .Options;
        await using (var context = new NodeLocalSearchDbContext(options))
            await context.Database.EnsureCreatedAsync();

        var store = new NodeEfAuthorizationConfigurationStore(new FixedFactory(options), vocabulary);
        var writer = new AuthorizationDefinitionWriter(
            store,
            store,
            new AuthorizationDefinitionAdmission(vocabulary),
            new AuthorizationCapabilityBindingAdmission(),
            TestAuthorization.AllowGate(),
            TestInMemoryAuthorizationStores.GrantStore());
        var globalDefinition = Definition(
            Guid.Parse("dddddddd-0000-0000-0000-000000000004"),
            "/global",
            RoleReference.Administrator);
        var privateDefinition = Definition(
            Guid.Parse("eeeeeeee-0000-0000-0000-000000000005"),
            "/private",
            tenantRole.Role);

        await writer.WriteAsync(new InstallAuthorizationDefinition(globalDefinition));
        var tenantAuthority = new AuthorizationWriteContext(
            new ActorId("test:authorization-writer"),
            tenantA,
            DateTimeOffset.Parse("2026-09-02T12:00:00Z"));
        await writer.WriteAsync(new InstallAuthorizationDefinition(privateDefinition, tenantA), tenantAuthority);
        await writer.WriteAsync(
            new ReplaceAuthorizationDefinition(privateDefinition with { Revision = 2 }, tenantA),
            tenantAuthority);

        Assert.Equal(2, (await store.ListAsync(tenantA)).Count);
        Assert.Single(await store.ListAsync(tenantB));
        Assert.NotNull(await store.FindAsync(tenantA, privateDefinition.DefinitionId));
        Assert.Null(await store.FindAsync(tenantB, privateDefinition.DefinitionId));
        Assert.Null((await store.ReadStateAsync(privateDefinition.DefinitionId, tenantB)).Definition);
        Assert.NotNull(await store.FindAsync(tenantB, globalDefinition.DefinitionId));
        await using var verification = new NodeLocalSearchDbContext(options);
        Assert.All(
            await verification.AuthorizationDefinitions
                .Where(row => row.DefinitionId == privateDefinition.DefinitionId.Value.ToString())
                .Select(row => row.DeclaringTenantId)
                .ToArrayAsync(),
            declaringTenantId => Assert.Equal(tenantA.Value, declaringTenantId));
    }

    private static AuthorizationCapabilityDefinition Definition(Guid id, string scope, RoleReference role)
    {
        var operation = AuthorizationOperation.Parse(Permission.OrgManageSettings);
        return new AuthorizationCapabilityDefinition(
            new AuthorizationCapabilityDefinitionId(id),
            "package.test",
            1,
            operation,
            new PermissionAtom(operation, ScopeExpression.Parse(scope)),
            RoleBindingSet.Of(role));
    }

    private sealed class FixedFactory(DbContextOptions<NodeLocalSearchDbContext> options)
        : IDbContextFactory<NodeLocalSearchDbContext>
    {
        public NodeLocalSearchDbContext CreateDbContext() => new(options);

        public Task<NodeLocalSearchDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
