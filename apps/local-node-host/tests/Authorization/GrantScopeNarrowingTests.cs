using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.LocalNodeHost.Data.Search.Vector;
using Harborline.Api.LocalNodeHost.Tests.Search;

namespace Harborline.Api.LocalNodeHost.Tests.Authorization;

public sealed class GrantScopeNarrowingTests
{
    private static readonly TenantId Tenant = new("scope-narrowing");
    private static readonly ActorId Admin = new("admin");
    private static readonly ActorId Holder = new("holder");
    private static readonly DateTimeOffset At = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("ef")]
    [InlineData("memory")]
    public async Task Frozen_scope_transition_replaces_one_grant_preserving_role_subject_and_history(string kind)
    {
        await using var store = await Store.CreateAsync(kind);
        var original = Grant();
        await store.Grants.AppendAsync(Tenant, original);
        var epochBefore = await ((IGrantAuthorizationEpochReader)store.Grants).ReadAuthorizationEpochAsync(Tenant, Holder);
        var replacement = GrantId.New();

        var result = await store.Grants.NarrowScopeAsync(Tenant, original.GrantId,
            ScopeExpression.Parse("/records/m6-t433-allowed-record-1"), replacement, Revocation());

        Assert.NotNull(result);
        Assert.Equal(replacement, result.Reissued.GrantId);
        Assert.Equal(original.Role, result.Reissued.Role);
        Assert.Equal(original.Subject, result.Reissued.Subject);
        Assert.Equal(original.Validity.ValidTo, result.Reissued.Validity.ValidTo);
        Assert.Equal("/records/m6-t433-allowed-record-1", result.Reissued.Scope.Value);
        Assert.Equal(GrantStatus.Revoked, result.Revoked.Status);
        Assert.Equal("/records", result.Revoked.Scope.Value);
        var authority = new AuthorizationWriteContext(Holder, Tenant, At);
        var operation = AuthorizationOperation.Parse("records:read");
        var allowed = authority.Request(operation, "record", "m6-t433-allowed-record-1").Target.Scope;
        var outside = authority.Request(operation, "record", "m6-t433-outside-record-1").Target.Scope;
        Assert.True(original.Scope.Contains(allowed));
        Assert.True(original.Scope.Contains(outside));
        Assert.True(result.Reissued.Scope.Contains(allowed));
        Assert.False(result.Reissued.Scope.Contains(outside));
        Assert.True(result.Revoked.IsActiveAt(At.AddMinutes(-1)));
        Assert.False(result.Revoked.IsActiveAt(At));
        Assert.False(result.Reissued.IsActiveAt(At.AddMinutes(-1)));
        Assert.True(result.Reissued.IsActiveAt(At));
        Assert.True(await ((IGrantAuthorizationEpochReader)store.Grants).ReadAuthorizationEpochAsync(Tenant, Holder) > epochBefore);
        Assert.Equal(2, (await store.Grants.SnapshotAsync(Tenant)).Count);
    }

    [Theory]
    [InlineData("ef", "/")]
    [InlineData("ef", "/records")]
    [InlineData("ef", "/records-other")]
    [InlineData("memory", "/")]
    [InlineData("memory", "/records")]
    [InlineData("memory", "/records-other")]
    public async Task Wider_equal_or_sibling_scope_cannot_mutate_grant_or_epoch(string kind, string scope)
    {
        await using var store = await Store.CreateAsync(kind);
        var original = Grant();
        await store.Grants.AppendAsync(Tenant, original);
        var epoch = await ((IGrantAuthorizationEpochReader)store.Grants).ReadAuthorizationEpochAsync(Tenant, Holder);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.Grants.NarrowScopeAsync(
            Tenant, original.GrantId, ScopeExpression.Parse(scope), GrantId.New(), Revocation()));
        Assert.Equal(original, Assert.Single(await store.Grants.SnapshotAsync(Tenant)));
        Assert.Equal(epoch, await ((IGrantAuthorizationEpochReader)store.Grants).ReadAuthorizationEpochAsync(Tenant, Holder));
    }

    [Theory]
    [InlineData("ef")]
    [InlineData("memory")]
    public async Task A_duplicate_successor_id_rolls_back_both_legs(string kind)
    {
        await using var store = await Store.CreateAsync(kind);
        var original = Grant();
        await store.Grants.AppendAsync(Tenant, original);
        await Assert.ThrowsAnyAsync<Exception>(() => store.Grants.NarrowScopeAsync(Tenant, original.GrantId,
            ScopeExpression.Parse("/records/m6-t433-allowed-record-1"), original.GrantId, Revocation()));
        Assert.Equal(original, Assert.Single(await store.Grants.SnapshotAsync(Tenant)));
    }

    [Theory]
    [InlineData("ef")]
    [InlineData("memory")]
    public async Task Narrowing_last_administrator_cannot_leave_install_without_an_administrator(string kind)
    {
        await using var store = await Store.CreateAsync(kind);
        var original = Grant() with { Role = RoleReference.Administrator, Scope = ScopeExpression.Parse("/") };
        await store.Grants.AppendAsync(Tenant, original);
        await Assert.ThrowsAsync<LastAdministratorRefusedException>(() => store.Grants.NarrowScopeAsync(Tenant,
            original.GrantId, ScopeExpression.Parse("/records/m6-t433-allowed-record-1"), GrantId.New(), Revocation()));
        Assert.Equal(original, Assert.Single(await store.Grants.SnapshotAsync(Tenant)));
    }

    private static GrantRevocation Revocation() => new(Admin, At, new GrantReason(GrantReasonCodes.RevocationReview));
    private static AccessGrant Grant() => new(GrantId.New(), Tenant, Holder, AccessGrantAuthorizationSeed.MemberRole,
        ScopeExpression.Parse("/records"), GrantResidency.OnlineOnly,
        new GrantValidity(At.AddDays(-1), At.AddDays(1)), GranterKind.Person, Admin, At.AddDays(-1),
        new GrantProvenance(GrantSourceKind.Manual, new GrantReason(GrantReasonCodes.Manual), Admin), At.AddDays(-1));

    private sealed class Store(IGrantStore grants, SearchTestStore? database) : IAsyncDisposable
    {
        internal IGrantStore Grants => grants;
        internal static async Task<Store> CreateAsync(string kind)
        {
            if (kind == "memory") return new(TestInMemoryAuthorizationStores.GrantStore(), null);
            var database = await SearchTestStore.CreateAsync();
            return new(new NodeEfGrantStore(database.Factory), database);
        }
        public async ValueTask DisposeAsync() { if (database is not null) await database.DisposeAsync(); }
    }
}
