using System.Linq;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Enrollment;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Data.Search;
using Harborline.Api.LocalNodeHost.Data.Search.Vector;
using Harborline.Api.LocalNodeHost.Tests.Authorization;
using Harborline.Api.LocalNodeHost.Tests.Search;

using Microsoft.EntityFrameworkCore;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

/// <summary>
/// Ticket 294 slice 2a — ONE party key. The canonical party is the canonical tenant PRINCIPAL id
/// (<c>CanonicalPartyBinding.PrincipalUserId.Value</c>), carried in the roster's <c>PartyId</c> and in
/// the wire's <c>JoiningPartyId</c>. These tests hold the two stores to that one key end to end: an
/// admission through the REAL bridge keys the roster edge by the same value the grant store keys
/// <c>AccessGrant.Subject</c> by, an unresolvable party refuses at admission with a named reason and
/// writes nothing, and the roster plane and the admin surface answer about the same actor.
/// </summary>
public sealed class PartyIdentityUnificationTests
{
    private const string Tenant = "7e57aaaa-0000-0000-0000-000000000294";
    private const string Principal = "principal-294";
    private const string PeopleParty = "people-party-294";
    private const string Grant = "grant-294";
    private const string FounderPartyId = "founder";
    private static readonly Guid Team = Guid.Parse("7e57bbbb-0000-0000-0000-000000000294");
    private static readonly IOperationVerifier Verifier = new Ed25519Verifier();
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeMilliseconds(1_752_640_000_000);

    [Fact]
    [Trait("PlanCard", "294-s2a")]
    public async Task An_admission_keys_the_roster_and_the_grant_store_by_the_same_value()
    {
        await using var store = await SeedGrantAsync();
        var founder = NewIdentity();
        var joiner = NewIdentity();
        var roster = MemberRoster.Genesis(Team, FounderPartyId, founder.Signer, Verifier, Now, Guid.NewGuid());
        var (bridge, tokenId) = CreateBridge(store, roster, new FixedPartyReader(PeopleParty));

        // The enrollment presents the joiner's canonical tenant principal as its JoiningPartyId.
        var outcome = await bridge.AdmitOnFirstEnrollmentAsync(
            roster, FounderPartyId, founder.Signer, tokenId, Membership(),
            Principal, joiner.PrincipalId, cancellationToken: CancellationToken.None);

        Assert.True(outcome.Admitted);
        var admittedPartyId = Assert.Single(
            outcome.Roster!.Members, member => member.PublicKey.Equals(joiner.PrincipalId)).PartyId;

        // Both stores agree, by value, on one key — and it is NOT the People PartyId.
        await using var context = store.CreateContext();
        var subject = await context.Grants.AsNoTracking()
            .Where(row => row.GrantId == Grant).Select(row => row.SubjectId).SingleAsync();
        Assert.Equal(subject, admittedPartyId);
        Assert.NotEqual(PeopleParty, admittedPartyId);

        // And the gate decides for exactly that ActorId.
        var inputs = EffectiveMemberPermissions.Read(outcome.Roster, admittedPartyId, new ActorId(subject));
        Assert.True(inputs.Member);
        var decision = await TestAuthorization.Gate(true).DecideAsync(
            TestAuthorization.Write(new TenantId(Tenant), admittedPartyId, Now)
                .Request(AuthorizationOperation.Parse(TeamRolePermissions.RecordsRead), "record", "r-1")
                with { Roster = inputs with { RequireMember = true } });
        Assert.Equal(AuthorizationVerdict.Allowed, decision.Verdict);
    }

    [Fact]
    [Trait("PlanCard", "294-s2a")]
    public async Task An_unresolvable_party_is_refused_at_admission_with_a_named_reason()
    {
        await using var store = await SeedGrantAsync();
        var founder = NewIdentity();
        var joiner = NewIdentity();
        var roster = MemberRoster.Genesis(Team, FounderPartyId, founder.Signer, Verifier, Now, Guid.NewGuid());
        var (bridge, tokenId) = CreateBridge(store, roster, new NullPartyReader());

        var outcome = await bridge.AdmitOnFirstEnrollmentAsync(
            roster, FounderPartyId, founder.Signer, tokenId, Membership(),
            Principal, joiner.PrincipalId, cancellationToken: CancellationToken.None);

        Assert.False(outcome.Admitted);
        Assert.Equal("party_binding_mismatch", outcome.RefusalReason);
        Assert.Null(outcome.Roster);
        // Nothing written on either plane: no roster edge, and no grant minted for the joiner.
        Assert.False(roster.Contains(Principal));
        await using var context = store.CreateContext();
        Assert.Equal(1, await context.Grants.AsNoTracking().CountAsync());
    }

    [Fact]
    [Trait("PlanCard", "294-s2a")]
    public async Task A_joiner_presenting_the_people_party_key_space_is_refused_at_admission()
    {
        await using var store = await SeedGrantAsync();
        var founder = NewIdentity();
        var joiner = NewIdentity();
        var roster = MemberRoster.Genesis(Team, FounderPartyId, founder.Signer, Verifier, Now, Guid.NewGuid());
        var (bridge, tokenId) = CreateBridge(store, roster, new FixedPartyReader(PeopleParty));

        var outcome = await bridge.AdmitOnFirstEnrollmentAsync(
            roster, FounderPartyId, founder.Signer, tokenId, Membership(),
            PeopleParty, joiner.PrincipalId, cancellationToken: CancellationToken.None);

        Assert.False(outcome.Admitted);
        Assert.Equal("party_binding_mismatch", outcome.RefusalReason);
        Assert.False(roster.Contains(PeopleParty));
    }

    [Fact]
    [Trait("PlanCard", "294-s2a")]
    public async Task The_roster_plane_and_the_admin_surface_give_the_same_verdict_for_the_same_member()
    {
        await using var store = await SeedGrantAsync();
        var founder = NewIdentity();
        var joiner = NewIdentity();
        var roster = MemberRoster.Genesis(Team, FounderPartyId, founder.Signer, Verifier, Now, Guid.NewGuid());
        var (bridge, tokenId) = CreateBridge(store, roster, new FixedPartyReader(PeopleParty));
        var admitted = await bridge.AdmitOnFirstEnrollmentAsync(
            roster, FounderPartyId, founder.Signer, tokenId, Membership(),
            Principal, joiner.PrincipalId, cancellationToken: CancellationToken.None);
        Assert.True(admitted.Admitted);

        // The roster plane reads the member by its roster PartyId; the admin surface reads the same member
        // by the grant subject it holds. One key means these are the same string and the same verdict.
        await using var context = store.CreateContext();
        var subject = await context.Grants.AsNoTracking()
            .Where(row => row.GrantId == Grant).Select(row => row.SubjectId).SingleAsync();

        var act = AuthorizationOperation.Parse(TeamRolePermissions.RecordsWrite);
        var gate = TestAuthorization.Gate(request => request.Principal.Value == subject);

        var rosterPlane = await gate.DecideAsync(
            TestAuthorization.Write(new TenantId(Tenant), admitted.Roster!.Members
                    .Single(member => member.PublicKey.Equals(joiner.PrincipalId)).PartyId, Now)
                .Request(act, "record", "r-1") with
            {
                Roster = EffectiveMemberPermissions.Read(
                    admitted.Roster,
                    admitted.Roster.Members.Single(m => m.PublicKey.Equals(joiner.PrincipalId)).PartyId,
                    new ActorId(subject)) with { RequireMember = true, RequireGrantCoverage = true },
            });

        var adminSurface = await gate.DecideAsync(
            TestAuthorization.Write(new TenantId(Tenant), subject, Now).Request(act, "record", "r-1") with
            {
                Roster = EffectiveMemberPermissions.Read(admitted.Roster, subject, new ActorId(subject))
                    with { RequireMember = true, RequireGrantCoverage = true },
            });

        Assert.Equal(rosterPlane.Verdict, adminSurface.Verdict);
        Assert.Equal(AuthorizationVerdict.Allowed, adminSurface.Verdict);
    }

    // ── Harness ─────────────────────────────────────────────────────────────────────────────────

    private static (WebAdmittedMemberAtlasBridge Bridge, string TokenId) CreateBridge(
        SearchTestStore store, MemberRoster roster, ICanonicalPrincipalPartyReader partyReader)
    {
        var coordinator = new AdmissionCoordinator(
            Verifier, new InMemoryAdmissionTokenStore(), new FixedClock(Now));
        var invite = coordinator.CreateInvite(TeamTrustAnchor.FromRoster(roster));
        var bridge = new WebAdmittedMemberAtlasBridge(
            partyReader, store.Factory, coordinator, new InMemoryWebPairingInviteBindingStore(),
            new FixedClock(Now),
            new FixedAuthorizationClosure(PermissionAtomSet.From(
                new[] { TeamRolePermissions.RecordsRead, TeamRolePermissions.RecordsWrite }
                    .Select(operation => PermissionAtom.Parse($"{operation}@/")))));
        return (bridge, invite.TokenId);
    }

    private static async Task<SearchTestStore> SeedGrantAsync()
    {
        var store = await SearchTestStore.CreateAsync();
        await using var context = store.CreateContext();
        context.Grants.Add(new GrantRow
        {
            GrantId = Grant,
            TenantId = Tenant,
            // THE one key: the grant store's subject is the canonical tenant principal id.
            SubjectId = Principal,
            RoleVocabulary = AccessGrantAuthorizationSeed.MemberRole.Vocabulary,
            RoleName = AccessGrantAuthorizationSeed.MemberRole.Name,
            ScopeType = 0,
            ScopeValue = "/",
            Residency = 0,
            ValidityFromUnixMs = Now.AddMinutes(-1).ToUnixTimeMilliseconds(),
            Status = (int)GrantStatus.Active,
            GranterKind = (int)GranterKind.Person,
            GrantedBy = "issuer-294",
            GrantedAtUnixMs = Now.AddMinutes(-1).ToUnixTimeMilliseconds(),
            Source = (int)GrantSourceKind.Invitation,
            ReasonCode = GrantReasonCodes.Invitation,
            Approver = "issuer-294",
            LastReviewedAtUnixMs = Now.AddMinutes(-1).ToUnixTimeMilliseconds(),
            OwnerVersion = 4,
        });
        context.GrantAuthorizationEpochs.Add(new GrantAuthorizationEpochRow
        {
            TenantId = Tenant,
            PrincipalId = Principal,
            AuthorizationEpoch = 7,
        });
        await context.SaveChangesAsync();
        return store;
    }

    private static TenantMembershipSnapshot Membership() =>
        new("membership-294", "account-294", Tenant, Principal, Grant,
            GrantOwnerVersion: 4, AuthorizationEpoch: 7, TenantMembershipStatus.Active, OwnerVersion: 3);

    private sealed record Signed(PrincipalId PrincipalId, IOperationSigner Signer);

    private static Signed NewIdentity()
    {
        var pair = KeyPair.Generate();
        return new Signed(pair.PrincipalId, new Ed25519Signer(pair));
    }

    private sealed class FixedPartyReader(string peoplePartyId) : ICanonicalPrincipalPartyReader
    {
        public ValueTask<CanonicalPartyBinding?> ResolveAsync(
            TenantId tenant, PrincipalUserId user, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<CanonicalPartyBinding?>(
                new CanonicalPartyBinding(tenant, user, new CanonicalPartyReference(peoplePartyId)));
    }

    private sealed class NullPartyReader : ICanonicalPrincipalPartyReader
    {
        public ValueTask<CanonicalPartyBinding?> ResolveAsync(
            TenantId tenant, PrincipalUserId user, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<CanonicalPartyBinding?>(null);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
