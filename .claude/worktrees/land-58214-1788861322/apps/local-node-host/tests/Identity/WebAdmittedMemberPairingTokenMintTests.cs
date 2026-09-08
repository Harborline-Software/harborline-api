using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Enrollment;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Data.Search;
using Harborline.Api.LocalNodeHost.Data.Search.Vector;
using Harborline.Api.LocalNodeHost.Tests.Search;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

/// <summary>
/// MTW-2 #3107 — the device-pairing token mint (<see cref="WebAdmittedMemberPairingTokenMint"/>) + the
/// bridge front door (<see cref="WebAdmittedMemberAtlasBridge.AdmitFromPairingTokenAsync"/>). Proves the
/// four web-plane pins are captured off the member's authenticated session-derived principal at mint,
/// bound to a single-use token, and that first-wire-enrollment admits ONLY when the enrollment matches the
/// bound party AND the live web-plane state still agrees — refusing, and signing NOTHING, on any drift.
/// The assertions claim enrollment/attestation integrity (a roster admission is never signed for pins no
/// authenticated web session bound, nor for live state that has drifted), NOT impersonation-resistance.
/// </summary>
public sealed class WebAdmittedMemberPairingTokenMintTests
{
    private const string TenantRaw = "7e57aaaa-0000-0000-0000-000000000001";
    private const string PrincipalId = "principal-1";
    private const string PartyId = "party-1";
    private const string GrantId = "grant-1";
    private const string FounderPartyId = "founder";
    private static readonly System.Guid TeamId =
        System.Guid.Parse("7e57bbbb-0000-0000-0000-000000000002");
    private static readonly IOperationVerifier Verifier = new Ed25519Verifier();
    private static readonly DateTimeOffset Now =
        DateTimeOffset.FromUnixTimeMilliseconds(1_752_640_000_000);

    // ── Happy path — mint under the session, then redeem at enrollment ──────────────────────────────

    [Fact]
    [Trait("PlanCard", "MTW-2-3107")]
    public async Task Mint_Then_Enrollment_Matching_The_Bound_Party_Admits()
    {
        await using var store = await SeedGrantAuthorityAsync(ownerVersion: 4, authorizationEpoch: 7);
        var founder = NewIdentity(FounderPartyId);
        var joiner = NewIdentity(PartyId);
        var roster = GenesisRoster(founder);
        var (mint, bridge) = CreatePair(store, new FixedPartyReader(PartyId));

        var minted = mint.MintForSession(Session(), roster);
        Assert.True(minted.Minted);
        Assert.NotNull(minted.Token);

        var outcome = await bridge.AdmitFromPairingTokenAsync(
            roster, FounderPartyId, founder.Signer, minted.Token!.TokenId,
            PartyId, joiner.Key.PrincipalId, cancellationToken: CancellationToken.None);

        Assert.True(outcome.Admitted);
        Assert.Null(outcome.RefusalReason);
        Assert.NotNull(outcome.Roster);
        Assert.True(outcome.Roster!.Contains(PartyId));
        Assert.Equal(joiner.Key.PrincipalId, outcome.Roster.PublicKeyOf(PartyId));
        Assert.True(outcome.Roster.ValidatesToGenesis(Verifier));
    }

    // ── Refusal paths ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("PlanCard", "MTW-2-3107")]
    public async Task Enrollment_Presenting_A_Party_The_Token_Was_Not_Minted_For_Refuses()
    {
        // The token is minted (under the session) for PartyId; the enrollment presents a DIFFERENT party.
        // The front door refuses BEFORE the single-use redemption, so the token is not consumed.
        await using var store = await SeedGrantAuthorityAsync(ownerVersion: 4, authorizationEpoch: 7);
        var founder = NewIdentity(FounderPartyId);
        var imposter = NewIdentity("party-imposter");
        var roster = GenesisRoster(founder);
        var (mint, bridge) = CreatePair(store, new FixedPartyReader(PartyId));

        var minted = mint.MintForSession(Session(), roster);

        var outcome = await bridge.AdmitFromPairingTokenAsync(
            roster, FounderPartyId, founder.Signer, minted.Token!.TokenId,
            "party-imposter", imposter.Key.PrincipalId, cancellationToken: CancellationToken.None);

        AssertRefused(outcome, "pairing_pin_mismatch", roster);
    }

    [Fact]
    [Trait("PlanCard", "MTW-2-3107")]
    public async Task Unknown_Pairing_Token_Refuses_And_Signs_Nothing()
    {
        await using var store = await SeedGrantAuthorityAsync(ownerVersion: 4, authorizationEpoch: 7);
        var founder = NewIdentity(FounderPartyId);
        var joiner = NewIdentity(PartyId);
        var roster = GenesisRoster(founder);
        var (_, bridge) = CreatePair(store, new FixedPartyReader(PartyId));

        // A token id no mint ever bound.
        var outcome = await bridge.AdmitFromPairingTokenAsync(
            roster, FounderPartyId, founder.Signer, "never-minted-token",
            PartyId, joiner.Key.PrincipalId, cancellationToken: CancellationToken.None);

        AssertRefused(outcome, "pairing_binding_unknown", roster);
    }

    [Fact]
    [Trait("PlanCard", "MTW-2-3107")]
    public async Task Grant_Revoked_After_Mint_Refuses_At_Redemption()
    {
        // The mint captures the session pins (it does not read the grant db); the grant is then revoked.
        // Redemption re-reads the LIVE grant and refuses — revocation between mint and enrollment holds.
        await using var store = await SeedGrantAuthorityAsync(
            ownerVersion: 4, authorizationEpoch: 7,
            revokedAtUnixMs: Now.AddMinutes(-1).ToUnixTimeMilliseconds());
        var founder = NewIdentity(FounderPartyId);
        var joiner = NewIdentity(PartyId);
        var roster = GenesisRoster(founder);
        var (mint, bridge) = CreatePair(store, new FixedPartyReader(PartyId));

        var minted = mint.MintForSession(Session(), roster);
        Assert.True(minted.Minted);

        var outcome = await bridge.AdmitFromPairingTokenAsync(
            roster, FounderPartyId, founder.Signer, minted.Token!.TokenId,
            PartyId, joiner.Key.PrincipalId, cancellationToken: CancellationToken.None);

        AssertRefused(outcome, "grant_unavailable", roster);
    }

    [Fact]
    [Trait("PlanCard", "MTW-2-3107")]
    public async Task Bound_Grant_Owner_Version_Comes_From_The_Session_And_Live_Drift_Refuses()
    {
        // The live grant is owner-version 5; the session pins owner-version 4. The mint binds the SESSION's
        // 4 (not the live 5), and the redemption re-read enforces the bound value against live — refuse.
        await using var store = await SeedGrantAuthorityAsync(ownerVersion: 5, authorizationEpoch: 7);
        var founder = NewIdentity(FounderPartyId);
        var joiner = NewIdentity(PartyId);
        var roster = GenesisRoster(founder);
        var (mint, bridge) = CreatePair(store, new FixedPartyReader(PartyId));

        var minted = mint.MintForSession(Session(grantOwnerVersion: 4), roster);

        var outcome = await bridge.AdmitFromPairingTokenAsync(
            roster, FounderPartyId, founder.Signer, minted.Token!.TokenId,
            PartyId, joiner.Key.PrincipalId, cancellationToken: CancellationToken.None);

        AssertRefused(outcome, "grant_unavailable", roster);
    }

    [Fact]
    [Trait("PlanCard", "MTW-2-3107")]
    public async Task Replayed_Pairing_Token_Refuses_A_Second_Admission()
    {
        await using var store = await SeedGrantAuthorityAsync(ownerVersion: 4, authorizationEpoch: 7);
        var founder = NewIdentity(FounderPartyId);
        var joiner = NewIdentity(PartyId);
        var roster = GenesisRoster(founder);
        var (mint, bridge) = CreatePair(store, new FixedPartyReader(PartyId));

        var minted = mint.MintForSession(Session(), roster);

        var first = await bridge.AdmitFromPairingTokenAsync(
            roster, FounderPartyId, founder.Signer, minted.Token!.TokenId,
            PartyId, joiner.Key.PrincipalId, cancellationToken: CancellationToken.None);
        Assert.True(first.Admitted);

        // Replay the SAME token against the post-admission roster — nothing is signed a second time. A pairing
        // token is pin-bound to ONE party, so a replay is necessarily for that party, which is now a member: the
        // F3 duplicate-party pre-check refuses (already_member) ahead of the token-store single-use rejection.
        // Both are fail-closed and both collapse to the one opaque wire refusal (F4). The token single-use gate
        // itself is proven directly in AdmissionProtocolTests.
        var replay = await bridge.AdmitFromPairingTokenAsync(
            first.Roster!, FounderPartyId, founder.Signer, minted.Token!.TokenId,
            PartyId, joiner.Key.PrincipalId, cancellationToken: CancellationToken.None);

        Assert.False(replay.Admitted);
        Assert.Equal("already_member", replay.RefusalReason);
        Assert.Null(replay.Roster);
    }

    [Fact]
    [Trait("PlanCard", "MTW-2-3107")]
    public void Principal_Construction_Refuses_Unexpected_Grant_Cardinality_Before_Mint()
    {
        var twoGrants = new List<PinnedGrantOwnerVersion>
        {
            new(GrantId, 4),
            new("grant-2", 1),
        };
        var exception = Assert.Throws<ArgumentException>(() =>
            Session(pinnedGrants: twoGrants));

        Assert.Equal(
            "Exactly one live grant pin is required. (Parameter 'pinnedGrantOwnerVersions')",
            exception.Message);
    }

    // ── Harness ─────────────────────────────────────────────────────────────────────────────────────

    private static (WebAdmittedMemberPairingTokenMint Mint, WebAdmittedMemberAtlasBridge Bridge) CreatePair(
        SearchTestStore store, ICanonicalPrincipalPartyReader partyReader)
    {
        // Mint and bridge share ONE token store (single-use gate) and ONE binding store (keyed pin lookup),
        // so a token minted here is redeemable — and its bound pins visible — at the bridge.
        var coordinator = new AdmissionCoordinator(
            Verifier, new InMemoryAdmissionTokenStore(), new FixedTimeProvider(Now));
        var bindings = new InMemoryWebPairingInviteBindingStore();
        var mint = new WebAdmittedMemberPairingTokenMint(coordinator, bindings);
        var bridge = new WebAdmittedMemberAtlasBridge(
            partyReader, store.Factory, coordinator, bindings, new FixedTimeProvider(Now),
            new FixedAuthorizationClosure());
        return (mint, bridge);
    }

    private static SelectedSessionRequestPrincipal Session(
        long grantOwnerVersion = 4,
        List<PinnedGrantOwnerVersion>? pinnedGrants = null) =>
        new(
            accountId: "account-1",
            tenantId: new TenantId(System.Guid.Parse(TenantRaw).ToString("D")),
            principalUserId: new PrincipalUserId(PrincipalId),
            canonicalParty: new CanonicalPartyReference(PartyId),
            membershipId: "membership-1",
            membershipOwnerVersion: 3,
            pinnedGrantOwnerVersions: pinnedGrants
                ?? new List<PinnedGrantOwnerVersion> { new(GrantId, grantOwnerVersion) },
            authorizationEpoch: 7,
            sessionCorrelationId: "session-corr-1",
            coordinationCorrelationId: "coord-corr-1");

    private static void AssertRefused(
        WebAdmittedMemberAtlasBridge.AtlasAdmissionOutcome outcome, string reason, MemberRoster roster)
    {
        Assert.False(outcome.Admitted);
        Assert.Equal(reason, outcome.RefusalReason);
        Assert.Null(outcome.Roster);
        // Nothing signed: the admitter's roster still holds only the founder, never the joiner.
        Assert.False(roster.Contains(PartyId));
    }

    private static async Task<SearchTestStore> SeedGrantAuthorityAsync(
        long ownerVersion, long authorizationEpoch, long? revokedAtUnixMs = null)
    {
        var store = await SearchTestStore.CreateAsync();
        await using var context = store.CreateContext();
        context.Grants.Add(new GrantRow
        {
            GrantId = GrantId,
            TenantId = new TenantId(System.Guid.Parse(TenantRaw).ToString("D")).Value,
            SubjectId = PrincipalId,
            RoleVocabulary = AccessGrantAuthorizationSeed.MemberRole.Vocabulary,
            RoleName = AccessGrantAuthorizationSeed.MemberRole.Name,
            ScopeValue = "/",
            Residency = 0,
            ValidityFromUnixMs = Now.AddMinutes(-1).ToUnixTimeMilliseconds(),
            GrantedBy = "issuer-1",
            GrantedAtUnixMs = Now.AddMinutes(-1).ToUnixTimeMilliseconds(),
            GranterKind = (int)GranterKind.Person,
            Source = (int)GrantSourceKind.Manual,
            Approver = "issuer-1",
            LastReviewedAtUnixMs = Now.AddMinutes(-1).ToUnixTimeMilliseconds(),
            RevokedAtUnixMs = revokedAtUnixMs,
            OwnerVersion = ownerVersion,
        });
        context.GrantAuthorizationEpochs.Add(new GrantAuthorizationEpochRow
        {
            TenantId = new TenantId(System.Guid.Parse(TenantRaw).ToString("D")).Value,
            PrincipalId = PrincipalId,
            AuthorizationEpoch = authorizationEpoch,
        });
        await context.SaveChangesAsync();
        return store;
    }

    private sealed record Identity(string PartyId, KeyPair Key, IOperationSigner Signer);

    private static Identity NewIdentity(string partyId)
    {
        var kp = KeyPair.Generate();
        return new Identity(partyId, kp, new Ed25519Signer(kp));
    }

    private static MemberRoster GenesisRoster(Identity founder) =>
        MemberRoster.Genesis(TeamId, founder.PartyId, founder.Signer, Verifier, Now, System.Guid.NewGuid());

    private sealed class FixedPartyReader(string partyId) : ICanonicalPrincipalPartyReader
    {
        public ValueTask<CanonicalPartyBinding?> ResolveAsync(
            TenantId tenant, PrincipalUserId user, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<CanonicalPartyBinding?>(
                new CanonicalPartyBinding(tenant, user, new CanonicalPartyReference(partyId)));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
