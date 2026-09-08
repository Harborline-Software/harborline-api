using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

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
using Harborline.Api.LocalNodeHost.Tests.Search;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

/// <summary>
/// MTW-2 #3107 — the first-wire-enrollment atlas bridge. Proves the bridge records a signed,
/// genesis-rooted roster admission for a web-admitted member ONLY when the four web-plane membership
/// pins (tenant, PrincipalUserId, PartyId, grant identity) agree with the identity presented at
/// enrollment, and refuses — signing NOTHING — on any pin mismatch. The assertions claim
/// enrollment/attestation integrity (the admitter never signs atlas presence for an enrollment that
/// does not match a live, Active web membership), NOT impersonation-resistance.
/// </summary>
public sealed class WebAdmittedMemberAtlasBridgeTests
{
    private const string TenantId = "7e57aaaa-0000-0000-0000-000000000001";
    private const string PrincipalId = "principal-1";
    private const string PartyId = "party-1";
    private const string GrantId = "grant-1";
    private const string FounderPartyId = "founder";
    private static readonly System.Guid TeamId =
        System.Guid.Parse("7e57bbbb-0000-0000-0000-000000000002");
    private static readonly IOperationVerifier Verifier = new Ed25519Verifier();
    private static readonly DateTimeOffset Now =
        DateTimeOffset.FromUnixTimeMilliseconds(1_752_640_000_000);

    // ── Happy path ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("PlanCard", "MTW-2-3107")]
    public async Task Matching_Pins_Record_A_Signed_Genesis_Rooted_Roster_Admission()
    {
        await using var store = await SeedGrantAuthorityAsync(ownerVersion: 4, authorizationEpoch: 7);
        var founder = NewIdentity(FounderPartyId);
        var joiner = NewIdentity(PartyId);
        var roster = GenesisRoster(founder);
        var (bridge, tokenId) = CreateBridge(store, roster, new FixedPartyReader(PartyId));

        var outcome = await bridge.AdmitOnFirstEnrollmentAsync(
            roster, FounderPartyId, founder.Signer, tokenId, Membership(),
            PartyId, joiner.Key.PrincipalId, cancellationToken: CancellationToken.None);

        Assert.True(outcome.Admitted);
        Assert.Null(outcome.RefusalReason);
        Assert.NotNull(outcome.Roster);
        // The signed, genesis-rooted admission row exists for the enrolling party, binding the exact
        // principal key the enrollment presented, and the roster still validates to genesis.
        Assert.True(outcome.Roster!.Contains(PartyId));
        Assert.Equal(joiner.Key.PrincipalId, outcome.Roster.PublicKeyOf(PartyId));
        Assert.True(outcome.Roster.ValidatesToGenesis(Verifier));
    }

    [Fact]
    public void Redeemed_Token_Decision_Refuses_A_Different_Team_With_The_Same_Decision()
    {
        var founder = NewIdentity(FounderPartyId);
        var joiner = NewIdentity(PartyId);
        var teamA = GenesisRoster(founder);
        var teamB = MemberRoster.Genesis(
            System.Guid.Parse("7e57bbbb-0000-0000-0000-000000000099"),
            founder.PartyId,
            founder.Signer,
            Verifier,
            Now,
            System.Guid.NewGuid());
        var coordinator = new AdmissionCoordinator(
            Verifier, new InMemoryAdmissionTokenStore(), new FixedTimeProvider(Now));
        var token = coordinator.CreateInvite(TeamTrustAnchor.FromRoster(teamA));
        var redemption = coordinator.RedeemForAdmission(token.TokenId);

        var result = coordinator.AdmitRedeemedInvite(
            teamB,
            redemption.Receipt!,
            FounderPartyId,
            founder.Signer,
            PartyId,
            joiner.Key.PrincipalId,
            PermissionCompositions.Member);

        Assert.False(result.Admitted_);
        Assert.Equal(InviteAdmissionResult.TokenTeamMismatch, result.RefusalCode);
        Assert.Same(redemption.Receipt, result.Decision);
        Assert.False(result.PreDecision);
        Assert.False(teamB.Contains(PartyId));
    }

    [Fact]
    [Trait("PlanCard", "MTW-2-3666")]
    public async Task Invitation_Selected_Permissions_Are_Carried_Into_The_Accepted_Roster_Edge()
    {
        var selected = PermissionSet.Of("records:read", "records:write");
        await using var store = await SeedGrantAuthorityAsync(
            ownerVersion: 4, authorizationEpoch: 7, permissions: selected);
        var founder = NewIdentity(FounderPartyId);
        var joiner = NewIdentity(PartyId);
        var roster = GenesisRoster(founder);
        var (bridge, tokenId) = CreateBridge(store, roster, new FixedPartyReader(PartyId), selected);

        var outcome = await bridge.AdmitOnFirstEnrollmentAsync(
            roster, FounderPartyId, founder.Signer, tokenId, Membership(),
            PartyId, joiner.Key.PrincipalId, cancellationToken: CancellationToken.None);

        Assert.True(outcome.Admitted);
        Assert.Equal(selected, outcome.Roster!.PermissionsOf(PartyId));
    }

    // ── Refusal paths (mismatched pins = NO admission) ──────────────────────────────────────────

    [Fact]
    [Trait("PlanCard", "MTW-2-3107")]
    public async Task Inactive_Membership_Refuses_And_Signs_Nothing()
    {
        await using var store = await SeedGrantAuthorityAsync(ownerVersion: 4, authorizationEpoch: 7);
        var founder = NewIdentity(FounderPartyId);
        var joiner = NewIdentity(PartyId);
        var roster = GenesisRoster(founder);
        var (bridge, tokenId) = CreateBridge(store, roster, new FixedPartyReader(PartyId));

        var outcome = await bridge.AdmitOnFirstEnrollmentAsync(
            roster, FounderPartyId, founder.Signer, tokenId,
            Membership(TenantMembershipStatus.Revoked),
            PartyId, joiner.Key.PrincipalId, cancellationToken: CancellationToken.None);

        AssertRefused(outcome, "membership_not_active", roster);
    }

    [Fact]
    [Trait("PlanCard", "MTW-2-3107")]
    public async Task Party_Presented_At_Enrollment_Not_Matching_The_Web_Binding_Refuses()
    {
        // The web binding is for PartyId; the enrollment presents a DIFFERENT party. No admission.
        await using var store = await SeedGrantAuthorityAsync(ownerVersion: 4, authorizationEpoch: 7);
        var founder = NewIdentity(FounderPartyId);
        var joiner = NewIdentity("party-imposter");
        var roster = GenesisRoster(founder);
        var (bridge, tokenId) = CreateBridge(store, roster, new FixedPartyReader(PartyId));

        var outcome = await bridge.AdmitOnFirstEnrollmentAsync(
            roster, FounderPartyId, founder.Signer, tokenId, Membership(),
            "party-imposter", joiner.Key.PrincipalId, cancellationToken: CancellationToken.None);

        AssertRefused(outcome, "party_binding_mismatch", roster);
        Assert.False(outcome.Roster is not null);
    }

    [Fact]
    [Trait("PlanCard", "MTW-2-3107")]
    public async Task Missing_Party_Binding_Refuses_And_Signs_Nothing()
    {
        await using var store = await SeedGrantAuthorityAsync(ownerVersion: 4, authorizationEpoch: 7);
        var founder = NewIdentity(FounderPartyId);
        var joiner = NewIdentity(PartyId);
        var roster = GenesisRoster(founder);
        var (bridge, tokenId) = CreateBridge(store, roster, new NullPartyReader());

        var outcome = await bridge.AdmitOnFirstEnrollmentAsync(
            roster, FounderPartyId, founder.Signer, tokenId, Membership(),
            PartyId, joiner.Key.PrincipalId, cancellationToken: CancellationToken.None);

        AssertRefused(outcome, "party_binding_mismatch", roster);
    }

    [Fact]
    [Trait("PlanCard", "MTW-2-3107")]
    public async Task Revoked_Grant_Refuses_Atlas_Admission()
    {
        // Grant revocation is the sufficient revocation lever — a revoked grant never earns atlas presence.
        await using var store = await SeedGrantAuthorityAsync(
            ownerVersion: 4, authorizationEpoch: 7,
            revokedAtUnixMs: Now.AddMinutes(-1).ToUnixTimeMilliseconds());
        var founder = NewIdentity(FounderPartyId);
        var joiner = NewIdentity(PartyId);
        var roster = GenesisRoster(founder);
        var (bridge, tokenId) = CreateBridge(store, roster, new FixedPartyReader(PartyId));

        var outcome = await bridge.AdmitOnFirstEnrollmentAsync(
            roster, FounderPartyId, founder.Signer, tokenId, Membership(),
            PartyId, joiner.Key.PrincipalId, cancellationToken: CancellationToken.None);

        AssertRefused(outcome, "grant_unavailable", roster);
    }

    [Fact]
    [Trait("PlanCard", "MTW-2-3107")]
    public async Task Grant_Owner_Version_Drift_Refuses()
    {
        await using var store = await SeedGrantAuthorityAsync(ownerVersion: 5, authorizationEpoch: 7);
        var founder = NewIdentity(FounderPartyId);
        var joiner = NewIdentity(PartyId);
        var roster = GenesisRoster(founder);
        var (bridge, tokenId) = CreateBridge(store, roster, new FixedPartyReader(PartyId));

        var outcome = await bridge.AdmitOnFirstEnrollmentAsync(
            roster, FounderPartyId, founder.Signer, tokenId, Membership(),
            PartyId, joiner.Key.PrincipalId, cancellationToken: CancellationToken.None);

        AssertRefused(outcome, "grant_unavailable", roster);
    }

    [Fact]
    [Trait("PlanCard", "MTW-2-3107")]
    public async Task Authorization_Epoch_Drift_Refuses()
    {
        await using var store = await SeedGrantAuthorityAsync(ownerVersion: 4, authorizationEpoch: 8);
        var founder = NewIdentity(FounderPartyId);
        var joiner = NewIdentity(PartyId);
        var roster = GenesisRoster(founder);
        var (bridge, tokenId) = CreateBridge(store, roster, new FixedPartyReader(PartyId));

        var outcome = await bridge.AdmitOnFirstEnrollmentAsync(
            roster, FounderPartyId, founder.Signer, tokenId, Membership(),
            PartyId, joiner.Key.PrincipalId, cancellationToken: CancellationToken.None);

        AssertRefused(outcome, "grant_unavailable", roster);
    }

    [Fact]
    [Trait("PlanCard", "MTW-2-3107")]
    public async Task Spent_Invite_Refuses_A_Second_Atlas_Admission()
    {
        // Pins match + the enrolling party is NOT yet a member, but the single-use wire invite was already
        // redeemed — fail-closed via the bridge's invite_rejected branch, nothing signed. (Spend the token on a
        // DIFFERENT party first, so the F3 duplicate-party pre-check does NOT short-circuit to already_member and
        // the token-store single-use rejection is what fails the second attempt.)
        await using var store = await SeedGrantAuthorityAsync(ownerVersion: 4, authorizationEpoch: 7);
        var founder = NewIdentity(FounderPartyId);
        var joiner = NewIdentity(PartyId);
        var other = NewIdentity("party-other");
        var roster = GenesisRoster(founder);

        var tokenStore = new InMemoryAdmissionTokenStore();
        var coordinator = new AdmissionCoordinator(Verifier, tokenStore, new FixedTimeProvider(Now));
        var bridge = new WebAdmittedMemberAtlasBridge(
            new FixedPartyReader(PartyId), store.Factory, coordinator,
            new InMemoryWebPairingInviteBindingStore(), new FixedTimeProvider(Now), new FixedAuthorizationClosure());
        var token = coordinator.CreateInvite(TeamTrustAnchor.FromRoster(roster));

        // Spend the token by admitting a DIFFERENT party directly, so PartyId is NOT yet a member but the token
        // is consumed.
        var withOther = coordinator.AdmitOverInvite(
            roster, token.TokenId, FounderPartyId, founder.Signer, "party-other", other.Key.PrincipalId,
            PermissionCompositions.Member);
        Assert.True(withOther.Admitted_);

        // Replay the SPENT token for the (fresh) PartyId — pins verify, pre-check passes, but the single-use gate
        // rejects the redemption. Fail-closed, nothing signed.
        var replay = await bridge.AdmitOnFirstEnrollmentAsync(
            withOther.Roster!, FounderPartyId, founder.Signer, token.TokenId, Membership(),
            PartyId, joiner.Key.PrincipalId, cancellationToken: CancellationToken.None);

        Assert.False(replay.Admitted);
        Assert.Equal("invite_rejected", replay.RefusalReason);
        Assert.Null(replay.Roster);
        Assert.False(withOther.Roster!.Contains(PartyId)); // nothing signed for PartyId
    }

    // ── #3141 gate F2 — DM + X-Wing key threading into the signed admission ─────────────────────

    [Fact]
    [Trait("PlanCard", "MTW-2-3141")]
    public async Task First_Admission_Binds_The_Enrolling_Device_Dm_And_XWing_Keys_Into_The_Signed_Admission()
    {
        // F2 (#1489 key-substitution fix). The web-first device's team-scoped DM + X-Wing confidentiality PUBLIC
        // keys MUST be threaded INTO the FIRST (one-shot, first-write-wins) signed admission, so peers get the
        // forge-proof (party → DM/X-Wing) bindings. Before this fix the bridge dropped them (defaulted to "").
        await using var store = await SeedGrantAuthorityAsync(ownerVersion: 4, authorizationEpoch: 7);
        var founder = NewIdentity(FounderPartyId);
        var joiner = NewIdentity(PartyId);
        var roster = GenesisRoster(founder);
        var (bridge, tokenId) = CreateBridge(store, roster, new FixedPartyReader(PartyId));

        var dmKeyB64 = DmKeyB64(0x40);       // the enrolling device's team-scoped DM public key
        var xwingKeyB64 = XWingKeyB64(0x40); // the enrolling device's team-scoped X-Wing public key

        var outcome = await bridge.AdmitOnFirstEnrollmentAsync(
            roster, FounderPartyId, founder.Signer, tokenId, Membership(),
            PartyId, joiner.Key.PrincipalId,
            joiningDmPublicKey: dmKeyB64, joiningXWingPublicKey: xwingKeyB64,
            cancellationToken: CancellationToken.None);

        Assert.True(outcome.Admitted);
        Assert.NotNull(outcome.Roster);
        // The keys are surfaced ONLY off the forge-proof SIGNED admission (DmPublicKeyOf / XWingPublicKeyOf read
        // AdmissionSignature.*), so this proves they were bound INTO what the admitter signed — not merely carried.
        var boundDm = outcome.Roster!.DmPublicKeyOf(PartyId);
        Assert.NotNull(boundDm);
        Assert.Equal(dmKeyB64, Harborline.Api.Foundation.Crypto.PrincipalId.FromBytes(boundDm!).ToBase64Url());
        var boundXWing = outcome.Roster.XWingPublicKeyOf(PartyId);
        Assert.NotNull(boundXWing);
        Assert.Equal(xwingKeyB64, ToRawB64Url(boundXWing!));
        Assert.True(outcome.Roster.ValidatesToGenesis(Verifier));
    }

    [Fact]
    [Trait("PlanCard", "MTW-2-3141")]
    public async Task First_Admission_Without_Presented_Keys_Binds_No_Dm_Or_XWing_Key()
    {
        // Contrast: a device that presents no DM / X-Wing key threads empty (→ suite #1), exactly as the existing
        // proximity/invite modes — the F2 change is purely additive, not a new hard requirement at this seam.
        await using var store = await SeedGrantAuthorityAsync(ownerVersion: 4, authorizationEpoch: 7);
        var founder = NewIdentity(FounderPartyId);
        var joiner = NewIdentity(PartyId);
        var roster = GenesisRoster(founder);
        var (bridge, tokenId) = CreateBridge(store, roster, new FixedPartyReader(PartyId));

        var outcome = await bridge.AdmitOnFirstEnrollmentAsync(
            roster, FounderPartyId, founder.Signer, tokenId, Membership(),
            PartyId, joiner.Key.PrincipalId, cancellationToken: CancellationToken.None);

        Assert.True(outcome.Admitted);
        Assert.Null(outcome.Roster!.DmPublicKeyOf(PartyId));
        Assert.Null(outcome.Roster.XWingPublicKeyOf(PartyId));
    }

    // ── #3141 gate F3 — fail-closed ordering (token-burn DoS) ───────────────────────────────────

    [Fact]
    [Trait("PlanCard", "MTW-2-3141")]
    public async Task Induced_Admit_Failure_Leaves_The_Pairing_Token_Redeemable()
    {
        // F3 (token-burn DoS fix). AdmitOverInvite redeems (consumes) the token BEFORE MemberRoster.Admit, and
        // Admit THROWS when the enrolling party is already a member (the "connect your device TWICE" case). The
        // bridge's duplicate-party pre-check refuses BEFORE the redemption, so the single-use token is NOT
        // consumed — it stays redeemable. (Before the fix, the second token would be BURNED by the failed attempt.)
        await using var store = await SeedGrantAuthorityAsync(ownerVersion: 4, authorizationEpoch: 7);
        var founder = NewIdentity(FounderPartyId);
        var joiner = NewIdentity(PartyId);
        var roster = GenesisRoster(founder);

        // Own the token store so we can prove — directly — that the token was not consumed by the failed attempt.
        var tokenStore = new InMemoryAdmissionTokenStore();
        var coordinator = new AdmissionCoordinator(Verifier, tokenStore, new FixedTimeProvider(Now));
        var bridge = new WebAdmittedMemberAtlasBridge(
            new FixedPartyReader(PartyId), store.Factory, coordinator,
            new InMemoryWebPairingInviteBindingStore(), new FixedTimeProvider(Now), new FixedAuthorizationClosure());
        var anchor = TeamTrustAnchor.FromRoster(roster);

        // Pre-seed: the enrolling party is ALREADY in the roster (first device already connected).
        var seedInvite = coordinator.CreateInvite(anchor);
        var seeded = coordinator.AdmitOverInvite(
            roster, seedInvite.TokenId, FounderPartyId, founder.Signer, PartyId, joiner.Key.PrincipalId,
            PermissionCompositions.Member);
        Assert.True(seeded.Admitted_);
        var rosterWithParty = seeded.Roster!;

        // A SECOND pairing token (a second "connect your device" action), still live.
        var secondToken = coordinator.CreateInvite(anchor);

        // The induced Admit failure: the party is already a member (Admit would throw inside AdmitOverInvite).
        var outcome = await bridge.AdmitOnFirstEnrollmentAsync(
            rosterWithParty, FounderPartyId, founder.Signer, secondToken.TokenId, Membership(),
            PartyId, joiner.Key.PrincipalId, cancellationToken: CancellationToken.None);

        Assert.False(outcome.Admitted);
        Assert.Equal("already_member", outcome.RefusalReason);
        Assert.Null(outcome.Roster);

        // THE TOKEN-BURN ASSERTION: the second token was NOT consumed by the failed attempt — it is STILL
        // redeemable. This is the whole point of moving the duplicate-party guard ahead of the redemption.
        Assert.True(tokenStore.Redeem(secondToken.TokenId, Now).Accepted);
    }

    [Fact]
    [Trait("PlanCard", "MTW-2-3141")]
    public async Task Residual_Admit_Guard_Failure_Refuses_Fail_Closed_And_Never_Throws()
    {
        // F3 fail-closed CONTRACT. If a residual Admit guard throws AFTER the pre-check — here the admitter's
        // SIGNER key does not match its roster binding — the bridge returns a coarse Refuse, NEVER lets the
        // RosterGuardException escape as an uncaught 500 / stack leak.
        await using var store = await SeedGrantAuthorityAsync(ownerVersion: 4, authorizationEpoch: 7);
        var founder = NewIdentity(FounderPartyId);
        var joiner = NewIdentity(PartyId);
        var wrongKeySigner = NewIdentity(FounderPartyId); // claims founder's party, but a DIFFERENT key
        var roster = GenesisRoster(founder);
        var (bridge, tokenId) = CreateBridge(store, roster, new FixedPartyReader(PartyId));

        var outcome = await bridge.AdmitOnFirstEnrollmentAsync(
            roster, FounderPartyId, wrongKeySigner.Signer, tokenId, Membership(),
            PartyId, joiner.Key.PrincipalId, cancellationToken: CancellationToken.None);

        Assert.False(outcome.Admitted);
        // The wrong-key path is the :304 "signing key does not match" guard → the benign availability FALLBACK.
        Assert.Equal("admitter_unavailable", outcome.RefusalReason);
        Assert.Null(outcome.Roster);
        Assert.False(roster.Contains(PartyId)); // nothing signed
    }

    [Fact]
    [Trait("PlanCard", "MTW-2-3141")]
    public async Task Crypto_Integrity_Guard_Failure_Is_Audited_Distinctly_From_The_Fallback_But_Wire_Is_Identical()
    {
        // Adversarial finding B: the catch(RosterGuardException) must NOT flatten a genuine cryptographic-integrity
        // failure ("Produced admission signature did not verify", MemberRoster.Admit) into the benign
        // admitter_unavailable audit reason. Distinct INTERNAL reasons; IDENTICAL wire bytes.
        await using var store = await SeedGrantAuthorityAsync(ownerVersion: 4, authorizationEpoch: 7);
        var founder = NewIdentity(FounderPartyId);
        var joiner = NewIdentity(PartyId);
        var roster = GenesisRoster(founder);

        // (a) crypto-integrity failure — a signer that PASSES the admitter-key-match guard (IssuerId == founder's
        //     roster key) but produces a signature that does NOT verify (it signs with a different key). This is
        //     the :341 "signature did not verify" throw, NOT the :304 key-mismatch guard.
        var brokenSigner = new BrokenSignatureSigner(founder.Key.PrincipalId, NewIdentity("other").Signer);
        var (bridgeA, tokenA) = CreateBridge(store, roster, new FixedPartyReader(PartyId));
        var cryptoFail = await bridgeA.AdmitOnFirstEnrollmentAsync(
            roster, FounderPartyId, brokenSigner, tokenA, Membership(),
            PartyId, joiner.Key.PrincipalId, cancellationToken: CancellationToken.None);

        // (b) the benign fallback — a wrong-key admitter (:304 guard).
        var (bridgeB, tokenB) = CreateBridge(store, roster, new FixedPartyReader(PartyId));
        var fallback = await bridgeB.AdmitOnFirstEnrollmentAsync(
            roster, FounderPartyId, NewIdentity(FounderPartyId).Signer, tokenB, Membership(),
            PartyId, joiner.Key.PrincipalId, cancellationToken: CancellationToken.None);

        // DISTINCT internal audit reasons — the crypto-integrity failure is NOT flattened into the fallback.
        Assert.Equal("admitter_signature_invalid", cryptoFail.RefusalReason);
        Assert.Equal("admitter_unavailable", fallback.RefusalReason);
        Assert.NotEqual(cryptoFail.RefusalReason, fallback.RefusalReason);

        // …yet the WIRE bytes are identical (F4 still collapses both to the one opaque refusal).
        Assert.False(cryptoFail.Admitted);
        Assert.False(fallback.Admitted);
        Assert.Equal(
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(cryptoFail.ToWire()),
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(fallback.ToWire()));
        Assert.Equal(
            WebAdmittedMemberAtlasBridge.AtlasAdmissionWireOutcome.OpaqueWireRefusal,
            cryptoFail.ToWire().Refusal);
    }

    // ── #3141 verify — the F3 pre-check does NOT over-block a roster-revoked party ──────────────

    [Fact]
    [Trait("PlanCard", "MTW-2-3141")]
    public async Task Roster_Revoked_Party_Is_Not_Counted_By_The_Pre_Check_So_Re_Enrollment_Is_Not_Over_Blocked()
    {
        // Adversarial "(verify, not asserted)": does the F3 roster.Contains pre-check count a REVOKED party as a
        // member, over-blocking legitimate re-enrollment? PINNED ANSWER: NO — this is intended, not an over-block.
        // MemberRoster.Revoke drops the party from LIVE state (_byParty.Remove, MemberRoster.cs), so
        // roster.Contains(revoked) is FALSE. The pre-check therefore PERMITS re-enrollment of a
        // roster-revoked-then-legitimately-re-invited party (with a still-live grant). The immutable admission log
        // is unaffected; only live membership is dropped. (If the inverse held — revoked-blocks-re-enroll — it
        // would be an availability over-block; it does not, so no behavior change and nothing to report.)
        await using var store = await SeedGrantAuthorityAsync(ownerVersion: 4, authorizationEpoch: 7);
        var founder = NewIdentity(FounderPartyId);
        var joiner = NewIdentity(PartyId);
        var roster = GenesisRoster(founder);
        var (bridge, tokenId) = CreateBridge(store, roster, new FixedPartyReader(PartyId));

        // Admit PartyId, then the founder (root-grant holder — no-bricking floor satisfied) revokes it.
        var admitted = await bridge.AdmitOnFirstEnrollmentAsync(
            roster, FounderPartyId, founder.Signer, tokenId, Membership(),
            PartyId, joiner.Key.PrincipalId, cancellationToken: CancellationToken.None);
        Assert.True(admitted.Admitted);
        Assert.True(admitted.Roster!.Contains(PartyId));

        var afterRevoke = admitted.Roster.Revoke(FounderPartyId, PartyId);

        // PINNED: a roster-revoked party is NOT a current member — the pre-check will not fire already_member.
        Assert.False(afterRevoke.Contains(PartyId));

        // End-to-end: a fresh token re-admits the revoked party (the pre-check permits it). Re-enrollment works.
        var (bridge2, tokenId2) = CreateBridge(store, afterRevoke, new FixedPartyReader(PartyId));
        var reEnroll = await bridge2.AdmitOnFirstEnrollmentAsync(
            afterRevoke, FounderPartyId, founder.Signer, tokenId2, Membership(),
            PartyId, joiner.Key.PrincipalId, cancellationToken: CancellationToken.None);
        Assert.True(reEnroll.Admitted);
        Assert.True(reEnroll.Roster!.Contains(PartyId));
    }

    // ── #3141 gate F4 — one opaque wire refusal (no CONTENT enumeration oracle) ─────────────────

    [Fact]
    [Trait("PlanCard", "MTW-2-3141")]
    public async Task All_Refusal_Paths_Produce_Byte_Identical_Wire_Responses()
    {
        // F4. The bridge keeps DISTINCT internal refusal reasons (audit/logging), but EVERY refusal path — across
        // both entry points, spanning unknown/mismatch/inactive/drifted/replayed/already-member/admitter-* —
        // MUST collapse to ONE opaque wire refusal, so a prober cannot distinguish them off the response BODY
        // (no CONTENT oracle; wire bytes equal). This proves body byte-identity only — timing-channel
        // equalization is a wiring-layer gate, not asserted here.
        var outcomes = await CollectDistinctRefusalOutcomesAsync();

        // Sanity: we actually exercised MANY distinct internal causes (else the collapse would be vacuous).
        var internalReasons = outcomes.Select(o => o.RefusalReason).ToHashSet(System.StringComparer.Ordinal);
        Assert.True(internalReasons.Count >= 6,
            $"expected ≥6 distinct internal refusal reasons; got {internalReasons.Count}: " +
            string.Join(",", internalReasons));

        // Project each to the WIRE and prove byte-identity — literally: serialize each wire response and assert
        // every byte sequence equals the first. No internal reason leaks; all refusals are indistinguishable.
        var wire = outcomes.Select(o => o.ToWire()).ToList();
        Assert.All(wire, w => Assert.False(w.Admitted));
        Assert.All(wire, w => Assert.Null(w.Roster));
        Assert.All(wire, w => Assert.Equal(
            WebAdmittedMemberAtlasBridge.AtlasAdmissionWireOutcome.OpaqueWireRefusal, w.Refusal));

        var wireBytes = wire
            .Select(w => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(w))
            .ToList();
        Assert.All(wireBytes, b => Assert.Equal(wireBytes[0], b));
        // And a single distinct wire value overall (structural record equality over the full wire form).
        Assert.Single(wire.Distinct());
    }

    /// <summary>Drive EVERY bridge refusal path (both entry points) and return the distinct refusal outcomes.</summary>
    private static async Task<List<WebAdmittedMemberAtlasBridge.AtlasAdmissionOutcome>>
        CollectDistinctRefusalOutcomesAsync()
    {
        var outcomes = new List<WebAdmittedMemberAtlasBridge.AtlasAdmissionOutcome>();
        var founder = NewIdentity(FounderPartyId);
        var joiner = NewIdentity(PartyId);
        var other = NewIdentity("party-other");

        // Live, Active-grant setup shared by most paths.
        await using (var store = await SeedGrantAuthorityAsync(ownerVersion: 4, authorizationEpoch: 7))
        {
            var roster = GenesisRoster(founder);
            var bindings = new InMemoryWebPairingInviteBindingStore();
            var tokenStore = new InMemoryAdmissionTokenStore();
            var coordinator = new AdmissionCoordinator(Verifier, tokenStore, new FixedTimeProvider(Now));
            var bridge = new WebAdmittedMemberAtlasBridge(
                new FixedPartyReader(PartyId), store.Factory, coordinator, bindings, new FixedTimeProvider(Now),
                new FixedAuthorizationClosure());
            var anchor = TeamTrustAnchor.FromRoster(roster);

            // (1) pairing_binding_unknown — a token id no mint ever bound (front door).
            outcomes.Add(await bridge.AdmitFromPairingTokenAsync(
                roster, FounderPartyId, founder.Signer, "never-minted",
                PartyId, joiner.Key.PrincipalId, cancellationToken: CancellationToken.None));

            // (2) pairing_pin_mismatch — a token bound to PartyId, enrollment presents a DIFFERENT party.
            var bound = coordinator.CreateInvite(anchor);
            bindings.Bind(new WebPairingInviteBinding(bound.TokenId, Membership(), PartyId, bound.Anchor));
            outcomes.Add(await bridge.AdmitFromPairingTokenAsync(
                roster, FounderPartyId, founder.Signer, bound.TokenId,
                "party-other", other.Key.PrincipalId, cancellationToken: CancellationToken.None));

            // (3) membership_not_active — a Revoked web membership never earns atlas presence.
            var t3 = coordinator.CreateInvite(anchor);
            outcomes.Add(await bridge.AdmitOnFirstEnrollmentAsync(
                roster, FounderPartyId, founder.Signer, t3.TokenId, Membership(TenantMembershipStatus.Revoked),
                PartyId, joiner.Key.PrincipalId, cancellationToken: CancellationToken.None));

            // (4) invite_rejected — pins verify + the party is NOT yet a member, but the invite was already
            //     redeemed. Spend the token on a DIFFERENT party first, so the F3 pre-check does not short-circuit
            //     to already_member and the token-store single-use rejection is what fails it.
            var spent = coordinator.CreateInvite(anchor);
            var withOther = coordinator.AdmitOverInvite(
                roster, spent.TokenId, FounderPartyId, founder.Signer, "party-other", other.Key.PrincipalId,
                PermissionCompositions.Member);
            outcomes.Add(await bridge.AdmitOnFirstEnrollmentAsync(
                withOther.Roster!, FounderPartyId, founder.Signer, spent.TokenId, Membership(),
                PartyId, joiner.Key.PrincipalId, cancellationToken: CancellationToken.None));

            // (5) already_member — F3 pre-check refusal (the enrolling party is already in the roster).
            var admitP = await bridge.AdmitOnFirstEnrollmentAsync(
                roster, FounderPartyId, founder.Signer, coordinator.CreateInvite(anchor).TokenId, Membership(),
                PartyId, joiner.Key.PrincipalId, cancellationToken: CancellationToken.None);
            outcomes.Add(await bridge.AdmitOnFirstEnrollmentAsync(
                admitP.Roster!, FounderPartyId, founder.Signer, coordinator.CreateInvite(anchor).TokenId,
                Membership(), PartyId, joiner.Key.PrincipalId, cancellationToken: CancellationToken.None));

            // (6) admitter_unavailable — F3 try/catch refusal (admitter signer key ≠ roster binding).
            var wrongKeySigner = NewIdentity(FounderPartyId);
            outcomes.Add(await bridge.AdmitOnFirstEnrollmentAsync(
                roster, FounderPartyId, wrongKeySigner.Signer, coordinator.CreateInvite(anchor).TokenId,
                Membership(), PartyId, joiner.Key.PrincipalId, cancellationToken: CancellationToken.None));

            // (7) party_binding_mismatch — the live Party binding does not resolve (NullPartyReader).
            var nullReaderBridge = new WebAdmittedMemberAtlasBridge(
                new NullPartyReader(), store.Factory, coordinator, bindings, new FixedTimeProvider(Now),
                new FixedAuthorizationClosure());
            outcomes.Add(await nullReaderBridge.AdmitOnFirstEnrollmentAsync(
                roster, FounderPartyId, founder.Signer, coordinator.CreateInvite(anchor).TokenId, Membership(),
                PartyId, joiner.Key.PrincipalId, cancellationToken: CancellationToken.None));
        }

        // (8) grant_unavailable — a revoked live grant refuses (its own store).
        await using (var revokedStore = await SeedGrantAuthorityAsync(
            ownerVersion: 4, authorizationEpoch: 7, revokedAtUnixMs: Now.AddMinutes(-1).ToUnixTimeMilliseconds()))
        {
            var roster = GenesisRoster(founder);
            var (bridge, tokenId) = CreateBridge(revokedStore, roster, new FixedPartyReader(PartyId));
            outcomes.Add(await bridge.AdmitOnFirstEnrollmentAsync(
                roster, FounderPartyId, founder.Signer, tokenId, Membership(),
                PartyId, joiner.Key.PrincipalId, cancellationToken: CancellationToken.None));
        }

        return outcomes;
    }

    // ── Harness ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>A 32-byte X25519-shaped DM public key (base64url) for <paramref name="seed"/> — the wire form
    /// AdmissionSignature.DmPublicKey carries.</summary>
    private static string DmKeyB64(byte seed)
    {
        var bytes = new byte[Harborline.Api.Foundation.Crypto.PrincipalId.LengthInBytes];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)(seed + i);
        return Harborline.Api.Foundation.Crypto.PrincipalId.FromBytes(bytes).ToBase64Url();
    }

    /// <summary>A 1216-byte X-Wing-shaped public key (base64url, no padding) for <paramref name="seed"/> — the
    /// wire form AdmissionSignature.XWingPublicKey carries.</summary>
    private static string XWingKeyB64(byte seed)
    {
        var bytes = new byte[1216];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)(seed + i);
        return ToRawB64Url(bytes);
    }

    /// <summary>base64url (no padding) encode of a raw byte array — matches the roster's X-Wing wire codec.</summary>
    private static string ToRawB64Url(byte[] bytes) =>
        System.Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static (WebAdmittedMemberAtlasBridge Bridge, string TokenId) CreateBridge(
        SearchTestStore store, MemberRoster roster, ICanonicalPrincipalPartyReader partyReader,
        PermissionSet? permissions = null)
    {
        var coordinator = new AdmissionCoordinator(
            Verifier, new InMemoryAdmissionTokenStore(), new FixedTimeProvider(Now));
        var invite = coordinator.CreateInvite(TeamTrustAnchor.FromRoster(roster));
        var bridge = new WebAdmittedMemberAtlasBridge(
            partyReader, store.Factory, coordinator,
            new InMemoryWebPairingInviteBindingStore(), new FixedTimeProvider(Now),
            permissions is null
                ? new FixedAuthorizationClosure()
                : new FixedAuthorizationClosure(PermissionAtomSet.From(
                    permissions.Permissions.Select(operation => PermissionAtom.Parse($"{operation}@/")))));
        return (bridge, invite.TokenId);
    }

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
        long ownerVersion,
        long authorizationEpoch,
        long? revokedAtUnixMs = null,
        PermissionSet? permissions = null)
    {
        var store = await SearchTestStore.CreateAsync();
        await using var context = store.CreateContext();
        context.Grants.Add(new GrantRow
        {
            GrantId = GrantId,
            TenantId = TenantId,
            SubjectId = PrincipalId,
            RoleVocabulary = AccessGrantAuthorizationSeed.MemberRole.Vocabulary,
            RoleName = AccessGrantAuthorizationSeed.MemberRole.Name,
            ScopeType = 0,
            ScopeValue = "/",
            Residency = 0,
            ValidityFromUnixMs = Now.AddMinutes(-1).ToUnixTimeMilliseconds(),
            Status = revokedAtUnixMs is null ? (int)GrantStatus.Active : (int)GrantStatus.Revoked,
            GranterKind = (int)GranterKind.Person,
            GrantedBy = "issuer-1",
            GrantedAtUnixMs = Now.AddMinutes(-1).ToUnixTimeMilliseconds(),
            Source = (int)GrantSourceKind.Invitation,
            ReasonCode = GrantReasonCodes.Invitation,
            Approver = "issuer-1",
            LastReviewedAtUnixMs = Now.AddMinutes(-1).ToUnixTimeMilliseconds(),
            RevokedBy = revokedAtUnixMs is null ? null : "issuer-1",
            RevokedAtUnixMs = revokedAtUnixMs,
            RevocationReasonCode = revokedAtUnixMs is null ? null : GrantReasonCodes.RevocationReview,
            OwnerVersion = ownerVersion,
        });
        context.GrantAuthorizationEpochs.Add(new GrantAuthorizationEpochRow
        {
            TenantId = TenantId,
            PrincipalId = PrincipalId,
            AuthorizationEpoch = authorizationEpoch,
        });
        await context.SaveChangesAsync();
        return store;
    }

    private static TenantMembershipSnapshot Membership(
        TenantMembershipStatus status = TenantMembershipStatus.Active) =>
        new(
            "membership-1",
            "account-1",
            TenantId,
            PrincipalId,
            GrantId,
            GrantOwnerVersion: 4,
            AuthorizationEpoch: 7,
            status,
            OwnerVersion: 3);

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

    private sealed class NullPartyReader : ICanonicalPrincipalPartyReader
    {
        public ValueTask<CanonicalPartyBinding?> ResolveAsync(
            TenantId tenant, PrincipalUserId user, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<CanonicalPartyBinding?>(null);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>
    /// A signer that PASSES MemberRoster.Admit's admitter-key-match guard (:304) — its <see cref="IssuerId"/>
    /// equals the admitter's roster key — but produces a signature that does NOT verify: it signs with a
    /// DIFFERENT key and stamps the claimed issuer. This induces the ":341 Produced admission signature did not
    /// verify" cryptographic-integrity guard, distinct from the :304 signing-key-mismatch guard.
    /// </summary>
    private sealed class BrokenSignatureSigner(
        Harborline.Api.Foundation.Crypto.PrincipalId claimedIssuer, IOperationSigner realOtherKeySigner)
        : IOperationSigner
    {
        public Harborline.Api.Foundation.Crypto.PrincipalId IssuerId => claimedIssuer;

        public async ValueTask<SignedOperation<T>> SignAsync<T>(
            T payload, DateTimeOffset issuedAt, Guid nonce, CancellationToken ct = default)
        {
            // Sign with the OTHER key, then claim the founder's issuer — the signature will not verify under it.
            var signed = await realOtherKeySigner.SignAsync(payload, issuedAt, nonce, ct).ConfigureAwait(false);
            return signed with { IssuerId = claimedIssuer };
        }
    }
}
