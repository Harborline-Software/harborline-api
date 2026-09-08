using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.LocalNodeHost.Data.Search.Vector;
using Harborline.Api.LocalNodeHost.Tests.Search;

namespace Harborline.Api.LocalNodeHost.Tests.Authorization;

/// <summary>
/// Ledger L619 — <c>not_last_administrator()</c> at the shared grant-mutation boundary. Every case runs
/// against BOTH production <see cref="IGrantStore"/> implementations, because the guard's promise is that
/// no mutation path on any store can strand the install.
/// </summary>
public sealed class LastAdministratorGuardTests
{
    private static readonly TenantId Tenant = TenantId.FromString("tenant-last-admin");
    private static readonly TenantId OtherTenant = TenantId.FromString("tenant-last-admin-other");
    private static readonly ActorId Admin = new("actor-last-admin");
    private static readonly ActorId Second = new("actor-last-admin-second");
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-09-06T12:00:00Z");
    private static readonly RoleReference Member = AccessGrantAuthorizationSeed.MemberRole;

    private static GrantRevocation Revocation(DateTimeOffset? at = null) => new(
        Admin, at ?? At, new GrantReason(GrantReasonCodes.RevocationOffboarding));

    [Theory]
    [InlineData("ef")]
    [InlineData("memory")]
    public async Task RevokingTheOnlyAdministratorInForceIsRefusedAndNothingIsWritten(string kind)
    {
        await using var harness = await StoreHarness.CreateAsync(kind);
        var only = GrantId.New();
        await harness.Store.AppendAsync(Tenant, Grant(only, Tenant, Admin, RoleReference.Administrator, "/"));
        await harness.Store.AppendAsync(Tenant, Grant(GrantId.New(), Tenant, Second, Member, "/"));

        var refusal = await Assert.ThrowsAsync<LastAdministratorRefusedException>(
            () => harness.Store.RevokeAsync(Tenant, only, Revocation()));

        Assert.Equal(only, refusal.Grant);
        Assert.Equal(Tenant, refusal.Tenant);
        Assert.Equal(At, refusal.At);
        var after = await harness.Store.FindAsync(Tenant, only);
        Assert.Equal(GrantStatus.Active, after!.Status);
        Assert.Null(after.Revocation);
    }

    /// <summary>
    /// 274 -- a padded copy of the administrator is not a second administrator. The padded spelling is
    /// refused by the type, and a case-variant OS-user spelling IS the same actor, so the guard still sees
    /// one administrator and refuses the revocation.
    /// </summary>
    [Theory]
    [InlineData("ef")]
    [InlineData("memory")]
    public async Task APaddedOrCaseVariantCopyOfTheAdministratorIsNotASecondAdministrator(string kind)
    {
        Assert.Throws<ArgumentException>(() => new ActorId(" actor-last-admin"));

        await using var harness = await StoreHarness.CreateAsync(kind);
        var osAdmin = new ActorId("os:admin#deadbeef");
        var only = GrantId.New();
        await harness.Store.AppendAsync(Tenant, Grant(only, Tenant, osAdmin, RoleReference.Administrator, "/"));
        await harness.Store.AppendAsync(
            Tenant, Grant(GrantId.New(), Tenant, new ActorId("os:Admin#deadbeef"), Member, "/"));

        await Assert.ThrowsAsync<LastAdministratorRefusedException>(
            () => harness.Store.RevokeAsync(Tenant, only, Revocation()));
        Assert.Equal(new ActorId("os:Admin#deadbeef"), osAdmin);
    }

    [Theory]
    [InlineData("ef")]
    [InlineData("memory")]
    public async Task RevokingOneOfTwoAdministratorsSucceeds(string kind)
    {
        await using var harness = await StoreHarness.CreateAsync(kind);
        var first = GrantId.New();
        await harness.Store.AppendAsync(Tenant, Grant(first, Tenant, Admin, RoleReference.Administrator, "/"));
        await harness.Store.AppendAsync(Tenant, Grant(GrantId.New(), Tenant, Second, RoleReference.Administrator, "/"));

        var revoked = await harness.Store.RevokeAsync(Tenant, first, Revocation());

        Assert.Equal(GrantStatus.Revoked, revoked!.Status);
    }

    [Theory]
    [InlineData("ef")]
    [InlineData("memory")]
    public async Task RevokingANonAdministratorGrantIsNeverGuarded(string kind)
    {
        await using var harness = await StoreHarness.CreateAsync(kind);
        var member = GrantId.New();
        await harness.Store.AppendAsync(Tenant, Grant(member, Tenant, Second, Member, "/"));

        var revoked = await harness.Store.RevokeAsync(Tenant, member, Revocation());

        Assert.Equal(GrantStatus.Revoked, revoked!.Status);
    }

    /// <summary>Expiry narrowing that would strand the install trips the same guard as a revocation.</summary>
    [Theory]
    [InlineData("ef")]
    [InlineData("memory")]
    public async Task NarrowingTheOnlyAdministratorsExpiryIsRefused(string kind)
    {
        await using var harness = await StoreHarness.CreateAsync(kind);
        var only = GrantId.New();
        await harness.Store.AppendAsync(Tenant, Grant(only, Tenant, Admin, RoleReference.Administrator, "/"));

        var refusal = await Assert.ThrowsAsync<LastAdministratorRefusedException>(
            () => harness.Store.ChangeValidityAsync(
                Tenant, only, new GrantValidity(At.AddHours(-2), At.AddMinutes(-1)),
                Admin, new GrantReason(GrantReasonCodes.Manual)));

        Assert.Equal(At.AddMinutes(-1), refusal.At);
        Assert.Equal(At.AddHours(2), (await harness.Store.FindAsync(Tenant, only))!.Validity.ValidTo);
    }

    /// <summary>Widening an expiry strands nobody, so the guard is a no-op on it.</summary>
    [Theory]
    [InlineData("ef")]
    [InlineData("memory")]
    public async Task WideningTheOnlyAdministratorsExpiryIsAllowed(string kind)
    {
        await using var harness = await StoreHarness.CreateAsync(kind);
        var only = GrantId.New();
        await harness.Store.AppendAsync(Tenant, Grant(only, Tenant, Admin, RoleReference.Administrator, "/"));

        var changed = await harness.Store.ChangeValidityAsync(
            Tenant, only, new GrantValidity(At.AddHours(-2), At.AddHours(9)),
            Admin, new GrantReason(GrantReasonCodes.Manual));

        Assert.Equal(At.AddHours(9), changed!.Validity.ValidTo);
    }

    /// <summary>
    /// "In force" is ticket 205's effective-grant reading: a revoked, expired, not-yet-effective,
    /// record-scoped or other-tenant Administrator is not a successor, so each of these near-misses still
    /// leaves the surviving grant the last one.
    /// </summary>
    [Theory]
    [InlineData("ef", "revoked")]
    [InlineData("ef", "expired")]
    [InlineData("ef", "not-yet-effective")]
    [InlineData("ef", "record-scoped")]
    [InlineData("ef", "other-tenant")]
    [InlineData("memory", "revoked")]
    [InlineData("memory", "expired")]
    [InlineData("memory", "not-yet-effective")]
    [InlineData("memory", "record-scoped")]
    [InlineData("memory", "other-tenant")]
    public async Task AnAdministratorGrantThatIsNotInForceIsNotASuccessor(string kind, string nearMiss)
    {
        await using var harness = await StoreHarness.CreateAsync(kind);
        var only = GrantId.New();
        await harness.Store.AppendAsync(Tenant, Grant(only, Tenant, Admin, RoleReference.Administrator, "/"));
        var near = GrantId.New();
        switch (nearMiss)
        {
            case "revoked":
                await harness.Store.AppendAsync(Tenant, Grant(near, Tenant, Second, RoleReference.Administrator, "/"));
                // A second in-force Administrator must exist for this revocation itself to be allowed.
                var bridge = GrantId.New();
                await harness.Store.AppendAsync(Tenant, Grant(bridge, Tenant, Admin, RoleReference.Administrator, "/"));
                await harness.Store.RevokeAsync(Tenant, near, Revocation(At.AddHours(-1)));
                await harness.Store.RevokeAsync(Tenant, bridge, Revocation(At.AddHours(-1)));
                break;
            case "expired":
                await harness.Store.AppendAsync(Tenant, Grant(near, Tenant, Second, RoleReference.Administrator, "/",
                    validFrom: At.AddHours(-3), validUntil: At.AddHours(-1)));
                break;
            case "not-yet-effective":
                await harness.Store.AppendAsync(Tenant, Grant(near, Tenant, Second, RoleReference.Administrator, "/",
                    validFrom: At.AddHours(1), validUntil: At.AddHours(4), grantedAt: At.AddHours(1)));
                break;
            case "record-scoped":
                await harness.Store.AppendAsync(Tenant, Grant(near, Tenant, Second, RoleReference.Administrator, "/records/a"));
                break;
            case "other-tenant":
                await harness.Store.AppendAsync(OtherTenant, Grant(near, OtherTenant, Second, RoleReference.Administrator, "/"));
                break;
        }

        await Assert.ThrowsAsync<LastAdministratorRefusedException>(
            () => harness.Store.RevokeAsync(Tenant, only, Revocation()));
        Assert.Equal(GrantStatus.Active, (await harness.Store.FindAsync(Tenant, only))!.Status);
    }

    /// <summary>
    /// The concurrency proof: two simultaneous revocations of the LAST TWO Administrators. The guard's
    /// population read runs inside the same unit of work as the write (the node's BEGIN IMMEDIATE fence
    /// transaction; the in-memory store's single lock), so the two are serialized and at least one
    /// Administrator survives. A check outside the write's lock would let both read "two remain" and commit.
    /// </summary>
    [Theory]
    [InlineData("ef")]
    [InlineData("memory")]
    public async Task TwoConcurrentRevocationsOfTheLastTwoAdministratorsLeaveOne(string kind)
    {
        await using var harness = await StoreHarness.CreateAsync(kind);
        var first = GrantId.New();
        var second = GrantId.New();
        await harness.Store.AppendAsync(Tenant, Grant(first, Tenant, Admin, RoleReference.Administrator, "/"));
        await harness.Store.AppendAsync(Tenant, Grant(second, Tenant, Second, RoleReference.Administrator, "/"));

        using var start = new Barrier(2);
        async Task<bool> RevokeAsync(GrantId id)
        {
            await Task.Yield();
            start.SignalAndWait();
            try
            {
                await harness.Store.RevokeAsync(Tenant, id, Revocation());
                return true;
            }
            catch (LastAdministratorRefusedException) { return false; }
            catch (Microsoft.Data.Sqlite.SqliteException) { return false; }
        }

        var outcomes = await Task.WhenAll(RevokeAsync(first), RevokeAsync(second));

        Assert.Contains(false, outcomes);
        var survivors = (await harness.Store.SnapshotAsync(Tenant))
            .Count(grant => LastAdministratorGuard.IsAdministratorInForce(grant, At));
        Assert.True(survivors >= 1, $"No Administrator survived; outcomes were [{string.Join(", ", outcomes)}].");
    }

    /// <summary>
    /// The population the boundary is handed is scanned tenant-by-tenant: another install's Administrator
    /// is never a successor here, whatever the caller passes in.
    /// </summary>
    [Fact]
    public void ThePopulationScanIgnoresAdministratorsOfAnotherTenant()
    {
        var only = Grant(GrantId.New(), Tenant, Admin, RoleReference.Administrator, "/");
        var foreign = Grant(GrantId.New(), OtherTenant, Second, RoleReference.Administrator, "/");
        var revoked = only with { Status = GrantStatus.Revoked, Revocation = Revocation() };

        Assert.Throws<LastAdministratorRefusedException>(
            () => LastAdministratorGuard.EnsureNotLastAdministrator(only, revoked, [only, foreign]));
        LastAdministratorGuard.EnsureNotLastAdministrator(
            only, revoked, [only, foreign with { TenantId = Tenant }]);
    }

    /// <summary>
    /// The window a grant is in force starts at the LATER of its <c>ValidFrom</c> and its <c>GrantedAt</c>.
    /// A backdated Administrator — issued at approval time with an earlier requested <c>ValidFrom</c>, the
    /// shape <c>GrantIssuanceHandler</c> produces — must be read from its <c>GrantedAt</c>; reading the bare
    /// <c>ValidFrom</c> answered an instant at which the grant was not yet in force and the guard silently
    /// checked nothing.
    /// </summary>
    [Theory]
    [InlineData("ef")]
    [InlineData("memory")]
    public async Task NarrowingTheValidityOfTheOnlyBackdatedAdministratorIsRefused(string kind)
    {
        await using var harness = await StoreHarness.CreateAsync(kind);
        var only = GrantId.New();
        await harness.Store.AppendAsync(Tenant, Grant(
            only, Tenant, Admin, RoleReference.Administrator, "/",
            validFrom: At.AddHours(-24), grantedAt: At.AddHours(-1)));

        var refusal = await Assert.ThrowsAsync<LastAdministratorRefusedException>(
            () => harness.Store.ChangeValidityAsync(
                Tenant, only, new GrantValidity(At.AddDays(30)),
                Admin, new GrantReason(GrantReasonCodes.Manual)));

        Assert.Equal(At.AddHours(-1), refusal.At);
        Assert.Equal(At.AddHours(-24), (await harness.Store.FindAsync(Tenant, only))!.Validity.ValidFrom);
    }

    /// <summary>
    /// Ledger L618 — the handover appends the successor and revokes the outgoing holder in one act, so the
    /// guard that refuses the bare revocation is satisfied rather than bypassed.
    /// </summary>
    [Theory]
    [InlineData("ef")]
    [InlineData("memory")]
    public async Task HandingOverTheOnlyAdministratorLeavesTheSuccessorInForce(string kind)
    {
        await using var harness = await StoreHarness.CreateAsync(kind);
        var only = GrantId.New();
        await harness.Store.AppendAsync(Tenant, Grant(only, Tenant, Admin, RoleReference.Administrator, "/"));
        var successor = Grant(
            GrantId.New(), Tenant, Second, RoleReference.Administrator, "/", validFrom: At, grantedAt: At);

        var handover = await harness.Store.HandoverAdministratorAsync(Tenant, only, successor, Revocation());

        Assert.Equal(successor.GrantId, handover!.Successor.GrantId);
        Assert.Equal(GrantStatus.Revoked, handover.Revoked.Status);
        Assert.Equal(GrantStatus.Revoked, (await harness.Store.FindAsync(Tenant, only))!.Status);
        var inForce = (await harness.Store.SnapshotAsync(Tenant))
            .Where(grant => LastAdministratorGuard.IsAdministratorInForce(grant, At))
            .ToArray();
        Assert.Equal(successor.GrantId, Assert.Single(inForce).GrantId);
    }

    /// <summary>
    /// A successor that is not in force at the revocation instant fails the guard INSIDE the handover's
    /// transaction — between the two legs — and rolls the whole act back: no successor grant, no revocation.
    /// </summary>
    [Theory]
    [InlineData("ef")]
    [InlineData("memory")]
    public async Task AHandoverToASuccessorThatIsNotYetInForceWritesNeitherLeg(string kind)
    {
        await using var harness = await StoreHarness.CreateAsync(kind);
        var only = GrantId.New();
        await harness.Store.AppendAsync(Tenant, Grant(only, Tenant, Admin, RoleReference.Administrator, "/"));
        var successor = Grant(
            GrantId.New(), Tenant, Second, RoleReference.Administrator, "/",
            validFrom: At.AddHours(1), grantedAt: At.AddHours(1));

        await Assert.ThrowsAsync<LastAdministratorRefusedException>(
            () => harness.Store.HandoverAdministratorAsync(Tenant, only, successor, Revocation()));

        Assert.Equal(GrantStatus.Active, (await harness.Store.FindAsync(Tenant, only))!.Status);
        Assert.Null(await harness.Store.FindAsync(Tenant, successor.GrantId));
    }

    /// <summary>
    /// A fault on the successor leg — here a grant id that already exists — takes the revocation down with
    /// it. Both legs are one unit of work; neither can land alone.
    /// </summary>
    [Theory]
    [InlineData("ef")]
    [InlineData("memory")]
    public async Task AFailureOnTheSuccessorLegRollsTheRevocationBack(string kind)
    {
        await using var harness = await StoreHarness.CreateAsync(kind);
        var only = GrantId.New();
        var taken = GrantId.New();
        await harness.Store.AppendAsync(Tenant, Grant(only, Tenant, Admin, RoleReference.Administrator, "/"));
        await harness.Store.AppendAsync(Tenant, Grant(taken, Tenant, Second, RoleReference.Administrator, "/"));
        var successor = Grant(taken, Tenant, Second, RoleReference.Administrator, "/", validFrom: At, grantedAt: At);

        await Assert.ThrowsAnyAsync<Exception>(
            () => harness.Store.HandoverAdministratorAsync(Tenant, only, successor, Revocation()));

        Assert.Equal(GrantStatus.Active, (await harness.Store.FindAsync(Tenant, only))!.Status);
        Assert.Equal(At.AddHours(-2), (await harness.Store.FindAsync(Tenant, taken))!.Validity.ValidFrom);
    }

    /// <summary>Handing over a grant this tenant does not have writes nothing and answers null.</summary>
    [Theory]
    [InlineData("ef")]
    [InlineData("memory")]
    public async Task HandingOverAnUnknownGrantAnswersNull(string kind)
    {
        await using var harness = await StoreHarness.CreateAsync(kind);
        var successor = Grant(
            GrantId.New(), Tenant, Second, RoleReference.Administrator, "/", validFrom: At, grantedAt: At);

        Assert.Null(await harness.Store.HandoverAdministratorAsync(
            Tenant, GrantId.New(), successor, Revocation()));
        Assert.Empty(await harness.Store.SnapshotAsync(Tenant));
    }

    private static AccessGrant Grant(
        GrantId id,
        TenantId tenant,
        ActorId principal,
        RoleReference role,
        string scope,
        DateTimeOffset? validFrom = null,
        DateTimeOffset? validUntil = null,
        DateTimeOffset? grantedAt = null) => new(
            id, tenant, principal, role, ScopeExpression.Parse(scope), GrantResidency.Cache,
            new GrantValidity(validFrom ?? At.AddHours(-2), validUntil ?? At.AddHours(2)),
            GranterKind.Person, Admin, grantedAt ?? At.AddHours(-2),
            new GrantProvenance(GrantSourceKind.Manual, new GrantReason(GrantReasonCodes.Manual), Admin),
            At.AddHours(-2));

    /// <summary>Either production grant store, behind one disposable handle.</summary>
    private sealed class StoreHarness(IGrantStore store, SearchTestStore? search) : IAsyncDisposable
    {
        public IGrantStore Store { get; } = store;

        public static async Task<StoreHarness> CreateAsync(string kind)
        {
            if (kind != "ef")
                return new StoreHarness(TestInMemoryAuthorizationStores.GrantStore(), null);
            var search = await SearchTestStore.CreateAsync();
            return new StoreHarness(new NodeEfGrantStore(search.Factory), search);
        }

        public async ValueTask DisposeAsync()
        {
            if (search is not null) await search.DisposeAsync();
        }
    }
}
