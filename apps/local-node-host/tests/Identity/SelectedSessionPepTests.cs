using System.Diagnostics;

using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Data.Roster;
using Harborline.Api.LocalNodeHost.Health.WebSession;

using Xunit.Abstractions;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

/// <summary>Mutation-honest coverage for the selected-session PEP and its epoch fence.</summary>
[Trait("PlanCard", "L2-3666")]
public sealed class SelectedSessionPepTests
{
    private const string FounderParty = "party-founder";
    private static readonly Guid TeamId = Guid.Parse("36660000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Now =
        new(2026, 8, 5, 16, 0, 0, TimeSpan.Zero);

    private readonly ITestOutputHelper _output;

    public SelectedSessionPepTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SelectedSessionSite_RecordsTheGateDecisionWithRosterInputs(bool allowed)
    {
        using var capture = new RosterDecisionCapture();
        var fixture = await Fixture.CreateAuditedAsync(capture.Audit, allowed ? PermissionSet.Of("records:read") : PermissionSet.Empty);
        Assert.Equal(allowed, await fixture.CheckAsync("member-a", "party-member-a", "session-a"));
        Assert.Equal(allowed ? 1 : 0, capture.Evidence.Count);
        Assert.All(capture.Evidence, evidence =>
        {
            Assert.True(evidence.Allowed);
            Assert.NotNull(evidence.Roster);
            Assert.Equal(PrincipalA, evidence.Roster.PartyId);
            Assert.True(evidence.Roster.Member);
            Assert.False(evidence.Roster.Ejected);
            Assert.Contains(evidence.Project()[1].Facts, fact => fact.StartsWith("roster:party:"));
        });
        Assert.Equal(allowed, capture.Evidence.Any(evidence => evidence.Allowed));
        await capture.AssertAuditAsync(fixture.Tenant);
    }

    [Fact(DisplayName = "records:read follows the conferred grant, not the pinned grant bundle")]
    public async Task Member_With_Permission_Is_Allowed_And_Member_Without_It_Is_Denied()
    {
        var fixture = await Fixture.CreateAsync(
            PermissionSet.Of("records:read"),
            PermissionSet.Empty);

        var withPermission = await fixture.CheckAsync("member-a", "party-member-a", "session-a");
        var withoutPermission = await fixture.CheckAsync("member-b", "party-member-b", "session-b");

        Assert.True(withPermission);
        Assert.False(withoutPermission);
        Assert.False(await fixture.CheckAsync(
            "member-a",
            "party-member-a",
            "session-a",
            permission: "grant-bundle-must-not-be-used"));
    }

    [Fact(DisplayName = "a narrowed conferred grant is denied after the authorization epoch advances")]
    public async Task Narrowing_And_Epoch_Bump_Denies()
    {
        var fixture = await Fixture.CreateAsync(PermissionSet.Of("records:read"));

        Assert.True(await fixture.CheckAsync("member-a", "party-member-a", "session-a"));

        fixture.NarrowConferredGrant(PrincipalA, PermissionSet.Empty);
        fixture.Epoch.Current = 2;

        Assert.False(await fixture.CheckAsync("member-a", "party-member-a", "session-a", epoch: 2));
    }

    [Fact(DisplayName = "a narrowed conferred grant is denied without an authorization epoch advance")]
    public async Task Narrowing_Without_Epoch_Bump_Denies()
    {
        var fixture = await Fixture.CreateAsync(PermissionSet.Of("records:read"));

        Assert.True(await fixture.CheckAsync("member-a", "party-member-a", "session-a"));

        // No epoch advance: the PEP must re-derive the conferred set on EVERY request rather than cache it by
        // epoch. That is the property the old roster-narrowing row held; the narrowing is now a grant one.
        fixture.NarrowConferredGrant(PrincipalA, PermissionSet.Empty);

        Assert.False(await fixture.CheckAsync("member-a", "party-member-a", "session-a"));
    }

    [Fact(DisplayName = "an ejected roster member never falls back to the grant bundle without an authorization epoch advance")]
    public async Task Ejected_Member_Without_Epoch_Bump_Denies()
    {
        var fixture = await Fixture.CreateAsync(PermissionSet.Of("records:read"));

        Assert.True(await fixture.CheckAsync("member-a", "party-member-a", "session-a"));

        fixture.Roster.Current = fixture.Roster.Current.Revoke(
            FounderParty,
            PrincipalA);

        Assert.False(await fixture.CheckAsync(
            "member-a",
            "party-member-a",
            "session-a",
            permission: "grant-bundle-must-not-be-used"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ejected_Gate_Principal_Is_Refused_When_Session_Presents_Canonical_Party(bool liveCanonicalEdge)
    {
        var fixture = await Fixture.CreateAsync(PermissionSet.Of("records:read"));
        using var founder = KeyPair.Generate();
        using var principalKey = KeyPair.Generate();
        using var partyKey = KeyPair.Generate();
        var signer = new Ed25519Signer(founder);
        var verifier = new Ed25519Verifier();
        var roster = MemberRoster.Genesis(TeamId, FounderParty, signer, verifier, Now, Guid.NewGuid())
            .Admit(FounderParty, signer, "principal-member-a", principalKey.PrincipalId,
                PermissionSet.Of("records:read"), verifier, Now, Guid.NewGuid());
        if (liveCanonicalEdge)
            roster = roster.Admit(FounderParty, signer, "party-member-a", partyKey.PrincipalId,
                PermissionSet.Of("records:read"), verifier, Now, Guid.NewGuid());
        fixture.Roster.Current = roster;
        Assert.True(await fixture.CheckAsync("member-a", "party-member-a", "session-a"));
        fixture.Roster.Current = roster.Revoke(FounderParty, "principal-member-a");
        Assert.False(await fixture.CheckAsync("member-a", "party-member-a", "session-a"));
    }

    [Fact(DisplayName = "a revoked grant denies even while the roster edge still has the permission")]
    public async Task Revoked_Grant_Is_Denied_Despite_Roster_Lag()
    {
        var fixture = await Fixture.CreateAsync(PermissionSet.Of("records:read"));

        Assert.True(await fixture.CheckAsync("member-a", "party-member-a", "session-a"));
        await fixture.Grants.RevokeAsync(
            fixture.Tenant,
            fixture.GrantIds["member-a"],
            expectedOwnerVersion: 1,
            revokedAt: Now);

        Assert.False(await fixture.CheckAsync("member-a", "party-member-a", "session-a"));
    }

    [Fact(DisplayName = "a live grant authorizes the deferred-admission principal when no roster admission exists")]
    public async Task Deferred_Admission_With_Live_Grant_Is_Allowed()
    {
        var roster = FounderOnlyRoster();
        var resolver = await BuildResolverAsync(
            new MutableRosterReader(roster),
            PermissionSet.Of("deferred:read"));

        var permissions = await resolver.Resolver.ResolveAsync(resolver.Principal);

        Assert.NotNull(permissions);
        Assert.True(permissions!.Contains("deferred:read"));
        Assert.DoesNotContain(
            "party-deferred",
            roster.EnumerateAdmissions().Select(admission => admission.PartyId));
    }

    [Fact(DisplayName = "a refused roster denies and never falls back to the live grant")]
    public async Task Refused_Roster_Does_Not_Use_Deferred_Grant_Fallback()
    {
        var resolver = await BuildResolverAsync(
            new RefusingRosterReader(),
            PermissionSet.Of("deferred:read"));

        var permissions = await resolver.Resolver.ResolveAsync(resolver.Principal);

        Assert.Null(permissions);
    }

    [Fact(DisplayName = "an unbound context fails closed")]
    public void No_Principal_Is_Denied()
    {
        var resolver = new FailClosedSelectedSessionPermissionResolver();
        var context = new SelectedSessionTenantContext(resolver);

        Assert.False(context.HasPermission("records:read"));
    }

    [Fact(DisplayName = "25-member roster rebuild timing is recorded")]
    public async Task TwentyFive_Member_Roster_Rebuild_Is_Measured()
    {
        using var founder = KeyPair.Generate();
        var verifier = new Ed25519Verifier();
        var founderSigner = new Ed25519Signer(founder);
        var roster = MemberRoster.Genesis(
            TeamId,
            FounderParty,
            founderSigner,
            verifier,
            Now,
            Guid.NewGuid());
        for (var index = 1; index < 25; index++)
        {
            using var member = KeyPair.Generate();
            roster = roster.Admit(
                FounderParty,
                founderSigner,
                $"principal-member-{index}",
                member.PrincipalId,
                PermissionSet.Of(Permission.ContactsRead),
                verifier,
                Now,
                Guid.NewGuid());
        }

        var tenant = new TenantId(TeamId.ToString("D"));
        var grants = TestInMemoryAuthorizationStores.GrantStore();
        var grantId = GrantId.New();
        await grants.SaveAsync(
            tenant,
            RoleGrant(grantId, tenant, new ActorId("principal-member-24")),
            expectedOwnerVersion: 0);

        var resolver = new SelectedSessionPermissionResolver(
            new RebuildingRosterReader(roster.EnumerateAdmissions(), verifier),
            grants,
            new FixedEpochReader(),
            new FixedTimeProvider(Now),
            NullLogger<SelectedSessionPermissionResolver>.Instance,
            TestAuthorization.ConferredGate(principal =>
                principal.Value == "principal-member-24"
                    ? PermissionSet.Of(Permission.ContactsRead)
                    : PermissionSet.Empty));
        var principal = new SelectedSessionRequestPrincipal(
            "account-member-24",
            tenant,
            new PrincipalUserId("principal-member-24"),
            new CanonicalPartyReference("party-member-24"),
            "membership-member-24",
            1,
            [new PinnedGrantOwnerVersion(grantId.ToString(), 1)],
            1,
            "session-member-24",
            "coordination-member-24");

        _ = await resolver.ResolveAsync(principal);
        var stopwatch = Stopwatch.StartNew();
        var permissions = await resolver.ResolveAsync(principal);
        stopwatch.Stop();

        Assert.NotNull(permissions);
        Assert.True(permissions!.Contains(Permission.ContactsRead));
        _output.WriteLine($"25-member cryptographic roster rebuild: {stopwatch.Elapsed.TotalMilliseconds:F2} ms");
    }

    [Fact(DisplayName = "two members in one tenant resolve only their own conferred sets")]
    public async Task Members_Do_Not_Bleed_Permissions_Across_Sessions()
    {
        var fixture = await Fixture.CreateAsync(
            PermissionSet.Of("records:read"),
            PermissionSet.Of("records:write"));

        var aReads = await fixture.CheckAsync("member-a", "party-member-a", "session-a");
        var aWrites = await fixture.CheckAsync(
            "member-a", "party-member-a", "session-a", permission: "records:write");
        var bReads = await fixture.CheckAsync(
            "member-b", "party-member-b", "session-b", permission: "records:read");
        var bWrites = await fixture.CheckAsync(
            "member-b", "party-member-b", "session-b", permission: "records:write");

        Assert.True(aReads);
        Assert.False(aWrites);
        Assert.False(bReads);
        Assert.True(bWrites);
    }

    private const string PrincipalA = "principal-member-a";
    private const string PrincipalB = "principal-member-b";

    private sealed class Fixture
    {
        private Fixture(
            MutableRosterReader roster,
            InMemoryGrantStore grants,
            MutableEpochReader epoch,
            TenantId tenant,
            Dictionary<string, GrantId> grantIds)
        {
            Roster = roster;
            Grants = grants;
            Epoch = epoch;
            Tenant = tenant;
            GrantIds = grantIds;
        }

        internal MutableRosterReader Roster { get; }
        internal InMemoryGrantStore Grants { get; }
        internal MutableEpochReader Epoch { get; }
        internal TenantId Tenant { get; }
        internal Dictionary<string, GrantId> GrantIds { get; }

        internal static Task<Fixture> CreateAsync(params PermissionSet[] memberPermissions) => CreateAuditedAsync(null, memberPermissions);

        internal static async Task<Fixture> CreateAuditedAsync(AuthorizationRefusalAudit? audit, params PermissionSet[] memberPermissions)
        {
            var founder = KeyPair.Generate();
            var memberA = KeyPair.Generate();
            var memberB = KeyPair.Generate();
            var verifier = new Ed25519Verifier();
            var signer = new Ed25519Signer(founder);
            var roster = MemberRoster.Genesis(
                    TeamId,
                    FounderParty,
                    signer,
                    verifier,
                    Now,
                    Guid.NewGuid())
                .Admit(
                    FounderParty,
                    signer,
                    PrincipalA,
                    memberA.PrincipalId,
                    memberPermissions.ElementAtOrDefault(0) ?? PermissionSet.Empty,
                    verifier,
                    Now,
                    Guid.NewGuid())
                .Admit(
                    FounderParty,
                    signer,
                    PrincipalB,
                    memberB.PrincipalId,
                    memberPermissions.ElementAtOrDefault(1) ?? PermissionSet.Empty,
                    verifier,
                    Now,
                    Guid.NewGuid());

            var tenant = new TenantId(TeamId.ToString("D"));
            var grants = TestInMemoryAuthorizationStores.GrantStore();
            var grantIds = new Dictionary<string, GrantId>(StringComparer.Ordinal);
            foreach (var (member, principal) in new[]
            {
                ("member-a", "principal-member-a"),
                ("member-b", "principal-member-b"),
            })
            {
                var grantId = GrantId.New();
                grantIds[member] = grantId;
                await grants.SaveAsync(
                    tenant,
                    RoleGrant(grantId, tenant, new ActorId(principal)),
                    expectedOwnerVersion: 0);
            }

            var rosterReader = new MutableRosterReader(roster);
            var epoch = new MutableEpochReader();
            // Ticket 293 slice 4 - GRANTS answer. Each member's admission conferred an install-root grant
            // carrying its permissions (NodeEfAuthorizationConfigurationStore.ConferAdmissionGrantAsync); this
            // mutable map IS that conferred authority, so narrowing a member here is a GRANT narrowing.
            var conferred = new Dictionary<string, PermissionSet>(StringComparer.Ordinal)
            {
                [PrincipalA] = memberPermissions.ElementAtOrDefault(0) ?? PermissionSet.Empty,
                [PrincipalB] = memberPermissions.ElementAtOrDefault(1) ?? PermissionSet.Empty,
            };
            var resolver = new SelectedSessionPermissionResolver(
                rosterReader,
                grants,
                epoch,
                new FixedTimeProvider(Now),
                NullLogger<SelectedSessionPermissionResolver>.Instance,
                TestAuthorization.ConferredGate(principal =>
                    conferred.TryGetValue(principal.Value, out var set) ? set : PermissionSet.Empty),
                audit);
            return new Fixture(rosterReader, grants, epoch, tenant, grantIds)
            {
                Resolver = resolver,
                Conferred = conferred,
            };
        }

        private ISelectedSessionPermissionResolver Resolver { get; init; } = null!;

        /// <summary>The conferred install-root grant set per principal - the authority the gate decides on.</summary>
        internal Dictionary<string, PermissionSet> Conferred { get; private init; } = null!;

        /// <summary>
        /// Narrow (or empty) the admitted party's CONFERRED grant: the wider set is revoked and the narrower one
        /// conferred under the same subject key. That is the production shape of "an administrator narrows a
        /// member" after ticket 293 slice 4 - the roster edge carries no permission set left to narrow.
        /// </summary>
        internal void NarrowConferredGrant(string principalId, PermissionSet narrower) =>
            Conferred[principalId] = narrower;

        internal async Task<bool> CheckAsync(
            string principalId,
            string partyId,
            string sessionId,
            long epoch = 1,
            string permission = "records:read")
        {
            var context = new SelectedSessionTenantContext(Resolver);
            await context.BindAsync(
                new SelectedSessionRequestPrincipal(
                    $"account-{principalId}",
                    Tenant,
                    new PrincipalUserId($"principal-{principalId}"),
                    new CanonicalPartyReference(partyId),
                    $"membership-{principalId}",
                    1,
                    [new PinnedGrantOwnerVersion(GrantIds[principalId].ToString(), 1)],
                    epoch,
                    sessionId,
                    $"coordination-{sessionId}"));
            return context.HasPermission(permission);
        }
    }

    private sealed class MutableRosterReader(MemberRoster current) : IVerifiedTenantRosterReader
    {
        internal MemberRoster Current { get; set; } = current;

        public Task<MemberRoster> ReadAsync(
            TenantId tenantId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Current);
    }

    private sealed class RebuildingRosterReader(
        IReadOnlyList<MemberAdmissionRecord> admissions,
        IOperationVerifier verifier) : IVerifiedTenantRosterReader
    {
        public Task<MemberRoster> ReadAsync(
            TenantId tenantId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(MemberRoster.FromSyncedRecords(admissions, [], verifier));
    }

    private static MemberRoster FounderOnlyRoster()
    {
        using var founder = KeyPair.Generate();
        var verifier = new Ed25519Verifier();
        return MemberRoster.Genesis(
            TeamId,
            FounderParty,
            new Ed25519Signer(founder),
            verifier,
            Now,
            Guid.NewGuid());
    }

    private static async Task<(SelectedSessionPermissionResolver Resolver,
        SelectedSessionRequestPrincipal Principal)> BuildResolverAsync(
        IVerifiedTenantRosterReader rosterReader,
        PermissionSet permissions)
    {
        var tenant = new TenantId(TeamId.ToString("D"));
        var grants = TestInMemoryAuthorizationStores.GrantStore();
        var grantId = GrantId.New();
        await grants.SaveAsync(
            tenant,
            RoleGrant(grantId, tenant, new ActorId("principal-deferred")),
            expectedOwnerVersion: 0);

        return (
            new SelectedSessionPermissionResolver(
                rosterReader,
                grants,
                new FixedEpochReader(),
                new FixedTimeProvider(Now),
                NullLogger<SelectedSessionPermissionResolver>.Instance,
                TestAuthorization.ConferredGate(principal =>
                    principal.Value == "principal-deferred" ? permissions : PermissionSet.Empty)),
            new SelectedSessionRequestPrincipal(
                "account-deferred",
                tenant,
                new PrincipalUserId("principal-deferred"),
                new CanonicalPartyReference("party-deferred"),
                "membership-deferred",
                1,
                [new PinnedGrantOwnerVersion(grantId.ToString(), 1)],
                1,
                "session-deferred",
                "coordination-deferred"));
    }

    private static AccessGrant RoleGrant(GrantId id, TenantId tenant, ActorId subject) => new(
        id, tenant, subject, AccessGrantAuthorizationSeed.MemberRole, ScopeExpression.Parse("/"),
        GrantResidency.Cache, new GrantValidity(Now - TimeSpan.FromHours(1)), GranterKind.Person,
        new ActorId("principal-founder"), Now - TimeSpan.FromHours(1),
        new GrantProvenance(GrantSourceKind.Manual, new GrantReason(GrantReasonCodes.Manual),
            new ActorId("principal-founder")), Now - TimeSpan.FromHours(1));

    private sealed class RefusingRosterReader : IVerifiedTenantRosterReader
    {
        public Task<MemberRoster> ReadAsync(
            TenantId tenantId,
            CancellationToken cancellationToken = default) =>
            throw new VerifiedTenantRosterRefusedException(
                VerifiedTenantRosterRefusal.Tampered,
                "test roster refusal");
    }

    private sealed class FixedEpochReader : ISelectedSessionAuthorizationEpochReader
    {
        public Task<long?> ReadAsync(
            TenantId tenantId,
            string principalId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<long?>(1);
    }

    private sealed class MutableEpochReader : ISelectedSessionAuthorizationEpochReader
    {
        internal long Current { get; set; } = 1;

        public Task<long?> ReadAsync(
            TenantId tenantId,
            string principalId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<long?>(Current);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
