using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Blocks.People.Foundation.Data;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Enrollment;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.Kernel.Crdt;
using Harborline.Api.Kernel.Crdt.Backends;
using Harborline.Api.Kernel.Security.Crypto;
using Harborline.Api.Kernel.Security.Keys;
using Harborline.Api.Kernel.Sync.Gossip;
using Harborline.Api.Kernel.Sync.Handshake;
using Harborline.Api.Kernel.Sync.Identity;
using Harborline.Api.Kernel.Sync.Protocol;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.People;
using Harborline.Api.LocalNodeHost.Data.Roster;
using Harborline.Api.LocalNodeHost.Enrollment;
using Harborline.Api.LocalNodeHost.Health;

// Disambiguate: the transport-layer signer is the kernel-security Ed25519Signer (it owns GenerateKeyPair + is the
// IEd25519Signer the gossip daemon + TeamSubkeyDerivation take). The foundation-crypto Ed25519Signer is the
// IOperationSigner used by NodePrincipalSigner internally (we never construct it directly here).
using Ed25519Signer = Harborline.Api.Kernel.Security.Crypto.Ed25519Signer;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// THE TEST EVERY PRIOR ONE SHOULD HAVE BEEN (cerebrum [2026-06-21]) — the two-sided WIRE ENROLLMENT trust
/// bootstrap, end-to-end, with TWO INDEPENDENT-GENESIS nodes and ZERO hand-provisioning of trust.
/// </summary>
/// <remarks>
/// <para>
/// <b>What every prior test did wrong (and hid the gap for the entire arc).</b> The "milestone" harness +
/// <see cref="EnrolledPeerConnectSyncTests"/> HAND-PROVISIONED trust: they gave each node the SAME team id, a
/// hand-seeded <c>MemberSetTrustPolicy</c> holding BOTH transport keys, and a shared roster — then proved comms
/// over that PRE-ESTABLISHED trust. They never exercised the BOOTSTRAP (B → a mutually-trusted member of A's team
/// over the wire). The headless production-route cross-machine verify caught it: two fresh nodes get
/// <c>PEER_UNTRUSTED</c> because the shipping admission was ADMITTER-NODE-LOCAL — A admits B into A's roster but
/// the admission never reaches B (roster-sync can't carry it; it needs an already-trusted session — chicken-and-
/// egg).
/// </para>
/// <para>
/// <b>What THIS test does (no hand-provisioning).</b> Two nodes with DISTINCT roots, DISTINCT own-teams, NO
/// hand-seeded trust, NO shared roster, NO pre-set <c>MemberSetTrustPolicy</c>:
/// <list type="number">
///   <item>A founds its team + mints a real invite (token + out-of-band team anchor).</item>
///   <item>B enrolls OVER THE WIRE via <see cref="NodeWireEnrollmentClient"/> — the ONLY trust input is the
///     invite + its anchor. B derives its A-team-scoped transport key, signs + sends the request to A's REAL
///     redeem logic, validates A's response against the anchor, and adopts A's team into B's roster + trust map.</item>
///   <item>After enrollment: BOTH nodes' <see cref="MemberSetTrustPolicy"/> (built from the post-enrollment
///     rosters — NOT hand-seeded) hold BOTH transport keys for A.teamId; the rosters converge (B in A's roster,
///     validates-to-genesis, on BOTH nodes); comms cross BOTH ways attributed to the correct enrolled party ids;
///     and a no-invite / forged peer is REJECTED (<c>PEER_UNTRUSTED</c>).</item>
/// </list>
/// </para>
/// <para>
/// <b>It FAILS on today's code (admitter-local) and PASSES after the build.</b> The
/// <see cref="EnrollmentBypassesBootstrap_ProvesTheGapExists"/> companion test pins the failing pre-build
/// behavior: with ONLY the shipping (A-side) admission and no joiner half, B's trust policy is empty toward A and
/// the handshake is rejected — the exact state the production verify hit.
/// </para>
/// </remarks>
public sealed class TwoSidedWireEnrollmentE2ETests : IAsyncLifetime
{
    private readonly List<IAsyncDisposable> _cleanup = new();
    private readonly List<string> _tempDirs = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var d in _cleanup)
        {
            try { await d.DisposeAsync(); } catch { /* best-effort */ }
        }
        foreach (var dir in _tempDirs)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch { /* best-effort temp cleanup */ }
        }
    }

    private static readonly TenantId LocalTenant = new("local");
    private static readonly PartyId Actor = new("operator");
    private static readonly IOperationVerifier Verifier = new Ed25519Verifier();
    private static readonly ITeamSubkeyDerivation SubkeyDerivation = new TeamSubkeyDerivation(new Ed25519Signer());
    private static readonly IXWingSubkeyDerivation XWingSubkeyDerivation =
        new HkdfXWingSubkeyDerivation(new XWingKem());

    // ───────────────────────────────────────────────────────────────────────────────────────────────────────
    // THE headline test — two independent-genesis nodes, enrollment over the wire, mutual trust + sync + reject.
    // ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "two-sided wire enrollment: two INDEPENDENT-genesis nodes establish mutual trust over the wire (NO hand-provisioning) → roster converges, comms cross both ways, forged peer rejected")]
    public async Task TwoIndependentNodes_EnrollOverTheWire_MutualTrust_RosterConverges_CommsCrossBothWays_ForgedRejected()
    {
        // ── A: an INDEPENDENT node. Its own root, its own genesis team, its own roster. No knowledge of B. ─────
        var a = NewNode("A", teamSeed: "team-A-office");
        // ── B: an INDEPENDENT node. DISTINCT root, DISTINCT own-team, DISTINCT genesis. No knowledge of A. ─────
        var b = NewNode("B", teamSeed: "team-B-home");

        // Sanity: they are genuinely independent — different team ids, different genesis parties, different roots.
        Assert.NotEqual(a.Roster.Current.TeamId, b.Roster.Current.TeamId);
        Assert.NotEqual(a.Roster.Current.GenesisPartyId, b.Roster.Current.GenesisPartyId);

        // Pre-enrollment: NEITHER node trusts the other for the wire. B's trust set toward A's team is empty
        // (no admitted peers), and A has never heard of B. This is the real two-fresh-nodes starting state.
        Assert.Empty(a.Roster.TrustedTransportKeys());
        Assert.Empty(b.Roster.TrustedTransportKeys());

        // ── A mints a REAL invite (token + the out-of-band team anchor B will pin). ─────────────────────────────
        var anchor = TeamTrustAnchor.FromRoster(a.Roster.Current);
        var invite = a.Coordinator.CreateInvite(anchor);

        // ── B ENROLLS over the wire. The ONLY trust input is the invite token + the anchor (the out-of-band pin).
        // The transport drives A's REAL redeem logic (verify B's signature → admit → build the bootstrap response)
        // — exactly what the /admission/redeem route does, in-process here instead of over HTTP.
        var transport = new InProcessAdmitterTransport(a);
        var outcome = await b.EnrollmentClient.EnrollAsync(invite.TokenId, anchor, transport, CancellationToken.None);

        Assert.True(outcome.Succeeded, $"B must enroll over the wire from the invite alone. Reason: {outcome.FailureReason}");
        Assert.Equal(a.Roster.Current.TeamId, outcome.TeamId);          // B adopted A's team.

        // ── MUTUAL TRUST: both nodes' trust policies now hold BOTH transport keys for A.teamId — built from the
        // post-enrollment rosters, NOT hand-seeded. ──────────────────────────────────────────────────────────
        // A's transport key for A.teamId (the floor) + B's transport key for A.teamId (the key B presented).
        var aTransportKey = a.TransportKeyForTeam(a.Roster.Current.TeamId);
        var bTransportKeyForATeam = b.EnrollmentClient.DeriveTransportPublicKeyForTeam(a.Roster.Current.TeamId.ToString("D"));
        Assert.True(outcome.TransportPublicKey!.AsSpan().SequenceEqual(bTransportKeyForATeam),
            "the key B reports presenting must be its A-team-scoped subkey (the #1296-F2 JOINED-team scope).");

        // A's trust set: own-floor (A's team subkey) ∪ admitted peer B's transport key.
        var aTrustKeys = UnionFloorAndPeers(aTransportKey, a.Roster);
        Assert.Contains(aTrustKeys, k => k.AsSpan().SequenceEqual(aTransportKey));            // A trusts itself.
        Assert.Contains(aTrustKeys, k => k.AsSpan().SequenceEqual(bTransportKeyForATeam));    // A trusts B.

        // B's trust set: own A-team subkey (floor, since B adopted A's team) ∪ A's transport key (from adoption).
        var bTrustKeys = UnionFloorAndPeers(bTransportKeyForATeam, b.Roster);
        Assert.Contains(bTrustKeys, k => k.AsSpan().SequenceEqual(bTransportKeyForATeam));    // B trusts itself.
        Assert.Contains(bTrustKeys, k => k.AsSpan().SequenceEqual(aTransportKey));            // B trusts A.

        // ── ROSTER CONVERGED: B is in A's roster (A admitted it) AND in B's adopted roster, both validate to A's
        // genesis, on BOTH nodes. ────────────────────────────────────────────────────────────────────────────
        Assert.True(a.Roster.Current.Contains(b.PartyId), "A's roster must contain B (A admitted B).");
        Assert.True(a.Roster.Current.ValidatesToGenesis(Verifier));
        Assert.True(b.Roster.Current.Contains(b.PartyId), "B's adopted roster must contain B.");
        Assert.True(b.Roster.Current.Contains(a.PartyId), "B's adopted roster must contain A (the admitter).");
        Assert.True(b.Roster.Current.ValidatesToGenesis(Verifier), "B's adopted roster must validate to A's genesis.");
        Assert.Equal(a.PartyId, b.Roster.Current.GenesisPartyId);      // B's adopted genesis = A's genesis.

        // ── COMMS CROSS BOTH WAYS over the REAL gossip wire, gated by MemberSetTrustPolicy built from the
        // post-enrollment rosters (NOT hand-seeded). Use the two distinct A-team transport identities. ─────────
        var deviceA = a.TeamScopedIdentity(a.Roster.Current.TeamId);                 // A's A-team identity.
        var deviceB = b.TeamScopedIdentity(a.Roster.Current.TeamId);                 // B's A-team identity (adopted).
        Assert.NotEqual(deviceA.PublicKey, deviceB.PublicKey);                       // distinct-root ⇒ distinct keys.

        var signer = new Ed25519Signer();

        // B LISTENS; its accept loop runs the MemberSetTrustPolicy backed by B's POST-ENROLLMENT roster.
        var transportBListen = new TcpSyncDaemonTransport("tcp://127.0.0.1:0");
        _cleanup.Add(transportBListen);
        var endpointB = transportBListen.ListenEndpoint!;
        var daemonB = BuildDaemon(transportBListen, deviceB, signer, b.Projection, TrustPolicyFor(deviceB.PublicKey, b.Roster));
        _cleanup.Add(daemonB);
        await daemonB.StartListeningAsync(CancellationToken.None);

        // A initiates; its trust gate is backed by A's POST-ENROLLMENT roster. AddPeer carries only the address.
        var transportAOut = new TcpSyncDaemonTransport();
        _cleanup.Add(transportAOut);
        var daemonA = BuildDaemon(transportAOut, deviceA, signer, a.Projection, TrustPolicyFor(deviceA.PublicKey, a.Roster));
        _cleanup.Add(daemonA);
        daemonA.AddPeer(endpointB, deviceB.PublicKey);
        await daemonA.StartAsync(CancellationToken.None);

        // A→B: a contact CREATEd on A reaches B's read store (the trusted session passed — NOT PEER_UNTRUSTED).
        var fromA = await CreateContactAsync(a, "Ada Lovelace");
        await WaitForEfAsync(b, fromA.Id, expected: "Ada Lovelace",
            because: "after wire enrollment, A→B comms must cross the trusted session (B trusts A's HELLO via the "
                + "adopted transport key — the bootstrap that never worked before).");

        // B→A: also crosses (mutual). B listens AND can dial A — start A listening too, B dials A.
        var transportAListen = new TcpSyncDaemonTransport("tcp://127.0.0.1:0");
        _cleanup.Add(transportAListen);
        var endpointA = transportAListen.ListenEndpoint!;
        var daemonAListen = BuildDaemon(transportAListen, deviceA, signer, a.Projection, TrustPolicyFor(deviceA.PublicKey, a.Roster));
        _cleanup.Add(daemonAListen);
        await daemonAListen.StartListeningAsync(CancellationToken.None);

        var transportBOut = new TcpSyncDaemonTransport();
        _cleanup.Add(transportBOut);
        var daemonBOut = BuildDaemon(transportBOut, deviceB, signer, b.Projection, TrustPolicyFor(deviceB.PublicKey, b.Roster));
        _cleanup.Add(daemonBOut);
        daemonBOut.AddPeer(endpointA, deviceA.PublicKey);
        await daemonBOut.StartAsync(CancellationToken.None);

        var fromB = await CreateContactAsync(b, "Grace Hopper");
        await WaitForEfAsync(a, fromB.Id, expected: "Grace Hopper",
            because: "after wire enrollment, B→A comms must also cross (mutual trust — both policies hold both keys).");

        await daemonA.StopAsync(CancellationToken.None);
        await daemonBOut.StopAsync(CancellationToken.None);
        await daemonB.StopListeningAsync(CancellationToken.None);
        await daemonAListen.StopListeningAsync(CancellationToken.None);

        // ── A FORGED / NON-INVITE peer is REJECTED. Charlie never enrolled (no invite) → not in any roster →
        // its A-team transport key is in NO trust set → PEER_UNTRUSTED. ───────────────────────────────────────
        var charlie = NewNode("Charlie", teamSeed: "team-A-office"); // even with A's team SEED, no enrollment.
        var deviceCharlie = charlie.TeamScopedIdentity(a.Roster.Current.TeamId);

        var transportBListen2 = new TcpSyncDaemonTransport("tcp://127.0.0.1:0");
        _cleanup.Add(transportBListen2);
        var endpointB2 = transportBListen2.ListenEndpoint!;
        var daemonB2 = BuildDaemon(transportBListen2, deviceB, signer, b.Projection, TrustPolicyFor(deviceB.PublicKey, b.Roster));
        _cleanup.Add(daemonB2);
        var bRejected = new SemaphoreSlim(0, 8);
        daemonB2.FrameReceived += (_, e) => { if (e.FrameType == GossipFrameType.HandshakeFailure) bRejected.Release(); };
        await daemonB2.StartListeningAsync(CancellationToken.None);

        var transportCharlie = new TcpSyncDaemonTransport();
        _cleanup.Add(transportCharlie);
        var daemonCharlie = BuildDaemon(transportCharlie, deviceCharlie, signer, charlie.Projection,
            new MemberSetTrustPolicy(new List<byte[]> { deviceCharlie.PublicKey }));
        _cleanup.Add(daemonCharlie);
        daemonCharlie.AddPeer(endpointB2, deviceB.PublicKey);
        var forged = await CreateContactAsync(charlie, "Exfiltration Attempt");
        await daemonCharlie.StartAsync(CancellationToken.None);

        Assert.True(await bRejected.WaitAsync(TimeSpan.FromSeconds(15)),
            "B must reject the non-invite peer Charlie (PEER_UNTRUSTED) — enrollment, not address, is trust.");

        await Task.Delay(1500);
        await using (var ctx = await b.Factory.CreateDbContextAsync())
        {
            var leaked = await ctx.Set<Party>().IgnoreQueryFilters().AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == forged.Id);
            Assert.Null(leaked);
        }

        await daemonCharlie.StopAsync(CancellationToken.None);
        await daemonB2.StopListeningAsync(CancellationToken.None);
    }

    // ───────────────────────────────────────────────────────────────────────────────────────────────────────
    // The pre-build PROOF: WITHOUT the joiner half (today's admitter-local admission), B's trust toward A is
    // empty and the handshake is PEER_UNTRUSTED. This is what the production verify hit. With the joiner half
    // (the headline test above) it passes. The two together pin "FAILS pre-build / PASSES post-build."
    // ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "PROOF the gap was real: with ONLY the shipping admitter-local admission (no joiner adoption), B does NOT trust A → handshake rejected")]
    public async Task EnrollmentBypassesBootstrap_ProvesTheGapExists()
    {
        var a = NewNode("A", teamSeed: "team-A-office");
        var b = NewNode("B", teamSeed: "team-B-home");

        // Simulate ONLY the shipping (pre-build) behavior: A admits B locally (admitter-node-local), and the
        // admission NEVER reaches B (no joiner adoption — the missing half). B keeps its OWN genesis team + empty
        // trust toward A.
        var anchor = TeamTrustAnchor.FromRoster(a.Roster.Current);
        var invite = a.Coordinator.CreateInvite(anchor);
        var bTransportForA = b.EnrollmentClient.DeriveTransportPublicKeyForTeam(a.Roster.Current.TeamId.ToString("D"));
        var admit = a.Coordinator.AdmitOverInvite(
            a.Roster.Current, invite.TokenId, a.PartyId, a.Signer.Signer, b.PartyId, b.Signer.Signer.IssuerId,
            PermissionCompositions.Member);
        Assert.True(admit.Admitted_);
        a.Roster.AdmitPeer(admit.Roster!, b.PartyId, bTransportForA); // A's local trust map updated (A trusts B).

        // B got NOTHING — its trust set toward A is empty (the admitter-local gap).
        Assert.Empty(b.Roster.TrustedTransportKeys());

        // Stand up the wire: A initiates to B. B's trust gate is its OWN (un-adopted) roster — it does NOT hold
        // A's transport key, so B rejects A's HELLO → PEER_UNTRUSTED. The exact production-verify failure.
        var signer = new Ed25519Signer();
        var deviceA = a.TeamScopedIdentity(a.Roster.Current.TeamId);                 // A's A-team identity.
        var deviceB = b.TeamScopedIdentity(a.Roster.Current.TeamId);                 // B's A-team identity (its HELLO key).

        var transportBListen = new TcpSyncDaemonTransport("tcp://127.0.0.1:0");
        _cleanup.Add(transportBListen);
        var endpointB = transportBListen.ListenEndpoint!;
        // B's trust gate = B's OWN roster (un-adopted) — it has NO peer transport keys, only B's own floor for
        // B's-OWN team, which is NOT even A's team. So it trusts neither A's key nor (here) its own A-team key.
        var bTrust = TrustPolicyFor(deviceB.PublicKey, b.Roster);
        var daemonB = BuildDaemon(transportBListen, deviceB, signer, b.Projection, bTrust);
        _cleanup.Add(daemonB);
        var bRejected = new SemaphoreSlim(0, 8);
        daemonB.FrameReceived += (_, e) => { if (e.FrameType == GossipFrameType.HandshakeFailure) bRejected.Release(); };
        await daemonB.StartListeningAsync(CancellationToken.None);

        var transportA = new TcpSyncDaemonTransport();
        _cleanup.Add(transportA);
        var daemonA = BuildDaemon(transportA, deviceA, signer, a.Projection, TrustPolicyFor(deviceA.PublicKey, a.Roster));
        _cleanup.Add(daemonA);
        daemonA.AddPeer(endpointB, deviceB.PublicKey);
        var party = await CreateContactAsync(a, "Should Not Cross");
        await daemonA.StartAsync(CancellationToken.None);

        Assert.True(await bRejected.WaitAsync(TimeSpan.FromSeconds(15)),
            "Without the joiner adoption (the admitter-local gap), B does NOT trust A's HELLO → PEER_UNTRUSTED. "
            + "This is the production-verify failure the joiner half closes.");

        await Task.Delay(1000);
        await using (var ctx = await b.Factory.CreateDbContextAsync())
        {
            var leaked = await ctx.Set<Party>().IgnoreQueryFilters().AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == party.Id);
            Assert.Null(leaked); // admitter-local admission → no comms cross → exactly the gap.
        }

        await daemonA.StopAsync(CancellationToken.None);
        await daemonB.StopListeningAsync(CancellationToken.None);
    }

    // ───────────────────────────────────────────────────────────────────────────────────────────────────────
    // #1304 F1 — THE SUPERSESSION-FAULT BITE. The fix is FAIL-CLOSED supersede-before-adopt: if the synced-doctype
    // supersession throws (its durable purge does an EF SaveChangesAsync that can throw DbUpdateException /
    // SqliteException), the JOIN must FAIL LOUD + RETRYABLE with a DISTINCT reason — NOT silently adopt A in memory
    // while leaving B's own genesis on the synced doctype (the persistent dual-genesis converge-never poison the
    // verdict named the BLOCKER). Drive the REAL EnrollAsync over the REAL admitter response with a supersession
    // that throws; assert (a) the enroll FAILS with roster_supersession_failed (not Succeeded, not a silent adopt),
    // and (b) B is left NON-POISONED — UNCHANGED on its OWN genesis team (no partial adopt, no half-state).
    //
    // PRE-FIX (the unhandled call at the old step 5b) the throw would propagate OUT of EnrollAsync after the
    // in-memory AdoptEnrollment had ALREADY run → B half-adopted + the exception escapes as a misleading 409/500 →
    // the exact silent-partial poison. POST-FIX EnrollAsync catches it, adopts nothing, and returns the loud reason.
    // ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "#1304 F1 SUPERSESSION-FAULT BITE: a throwing roster supersession makes the JOIN FAIL LOUD + retryable (roster_supersession_failed) and leaves B UNCHANGED on its own team — NOT a silent adopt + dual-genesis poison")]
    public async Task Supersession_Throw_FailsJoinLoud_LeavesBUnpoisoned()
    {
        // A founds its team + mints a real invite; the in-process transport drives A's REAL admit-and-respond.
        var a = NewNode("A", teamSeed: "team-A-office");
        var b = NewNode("B", teamSeed: "team-B-home");
        var anchor = TeamTrustAnchor.FromRoster(a.Roster.Current);
        var invite = a.Coordinator.CreateInvite(anchor);
        var transport = new InProcessAdmitterTransport(a);

        var bOwnTeamBefore = b.Roster.Current.TeamId;
        var bGenesisPartyBefore = b.Roster.Current.GenesisPartyId;
        Assert.NotEqual(a.Roster.Current.TeamId, bOwnTeamBefore); // a genuine cross-team join (supersession runs).

        // B's enrollment client wired with a supersession seam that THROWS (simulates the durable-purge
        // SaveChangesAsync faulting — DbUpdateException). EnrollAsync must NOT adopt; it must fail loud + retryable.
        var faulting = new ThrowingSupersession();
        var bClientFaulting = new NodeWireEnrollmentClient(
            b.RootIdentity, SubkeyDerivation, XWingSubkeyDerivation,
            b.Signer.Signer, b.PartyId, b.Roster, Verifier, faulting, clock: TimeProvider.System);

        var outcome = await bClientFaulting.EnrollAsync(invite.TokenId, anchor, transport, CancellationToken.None);

        // (a) LOUD + RETRYABLE + DISTINCT — not Succeeded, not a silent 200/409/500.
        Assert.False(outcome.Succeeded, "a supersession fault must FAIL the join (no silent adopt).");
        Assert.Equal("roster_supersession_failed", outcome.FailureReason);
        Assert.True(faulting.WasCalled, "the supersession must have been REACHED (the join is a genuine cross-team join).");

        // (b) B is UNCHANGED — still on its OWN genesis team, NOT half-adopted into A. No dual-genesis poison: B's
        //     in-memory roster never swapped, so nothing is inconsistent with the synced doctype.
        Assert.Equal(bOwnTeamBefore, b.Roster.Current.TeamId);
        Assert.Equal(bGenesisPartyBefore, b.Roster.Current.GenesisPartyId);
        Assert.False(b.Roster.Current.Contains(a.PartyId), "B must NOT have adopted A (the fault aborted the join).");
        Assert.Empty(b.Roster.TrustedTransportKeys());

        // RETRY proves recoverable: re-running the join from B's SAME unchanged own-team state with a HEALTHY
        // supersession now succeeds (the fault left no residue on B blocking a retry — atomic-ish: fully joins on
        // retry or cleanly didn't). A fresh admitter A2 (a re-issued invite — a new admit, since the first faulting
        // attempt already admitted B into the original A's roster) stands in for "operator re-runs the join".
        var a2 = NewNode("A2", teamSeed: "team-A2-office");
        var anchor2 = TeamTrustAnchor.FromRoster(a2.Roster.Current);
        var inviteRetry = a2.Coordinator.CreateInvite(anchor2);
        var transport2 = new InProcessAdmitterTransport(a2);
        var healthy = new CountingSupersession();
        var bClientHealthy = new NodeWireEnrollmentClient(
            b.RootIdentity, SubkeyDerivation, XWingSubkeyDerivation,
            b.Signer.Signer, b.PartyId, b.Roster, Verifier, healthy, clock: TimeProvider.System);
        var retry = await bClientHealthy.EnrollAsync(inviteRetry.TokenId, anchor2, transport2, CancellationToken.None);

        Assert.True(retry.Succeeded, $"a re-join after a supersession fault must succeed from B's clean state. Reason: {retry.FailureReason}");
        Assert.Equal(a2.Roster.Current.TeamId, retry.TeamId);
        Assert.True(healthy.WasCalled, "the retry must have run the supersession.");
        Assert.True(b.Roster.Current.Contains(a2.PartyId), "after the successful retry B has adopted A2's team.");
    }

    /// <summary>A supersession seam whose durable purge THROWS — simulates the EF SaveChangesAsync fault
    /// (DbUpdateException / SqliteException) the #1304 F1 fail-closed path must turn into a loud, retryable join
    /// failure rather than a silent partial adopt.</summary>
    private sealed class ThrowingSupersession : IOwnTeamRosterSupersession
    {
        public bool WasCalled { get; private set; }
        public Task<int> SupersedeOwnTeamRecordsAsync(Guid ownTeamId, CancellationToken ct)
        {
            WasCalled = true;
            throw new InvalidOperationException("simulated durable-purge SaveChangesAsync fault (DbUpdateException).");
        }
    }

    /// <summary>A healthy supersession seam (records that it ran) — the retry path's clean replacement.</summary>
    private sealed class CountingSupersession : IOwnTeamRosterSupersession
    {
        public bool WasCalled { get; private set; }
        public Task<int> SupersedeOwnTeamRecordsAsync(Guid ownTeamId, CancellationToken ct)
        {
            WasCalled = true;
            return Task.FromResult(0); // no synced doctype in this fixture — 0 records, but the call SUCCEEDS.
        }
    }

    // ───────────────────────────── node fixture (a self-contained independent node) ─────────────────────────

    /// <summary>An INDEPENDENT node fixture — its own root seed, genesis roster, admission seam, projection,
    /// and joiner enrollment client. No shared state with any other node (the no-hand-provisioning discipline).</summary>
    private sealed class NodeFixture
    {
        public required string Name { get; init; }
        public required byte[] RootSeed { get; init; }
        public required NodeIdentity RootIdentity { get; init; }
        public required NodePrincipalSigner Signer { get; init; }
        public required string PartyId { get; init; }
        public required NodeTeamRoster Roster { get; init; }
        public required AdmissionCoordinator Coordinator { get; init; }
        public required NodeWireEnrollmentClient EnrollmentClient { get; init; }
        public required IDbContextFactory<LocalNodeDbContext> Factory { get; init; }
        public required ContactCrdtProjection Projection { get; init; }

        /// <summary>This node's team-scoped transport PUBLIC key for a given team (HKDF(root, teamId)).</summary>
        public byte[] TransportKeyForTeam(Guid teamId) =>
            TeamScopedNodeIdentity.Derive(RootIdentity, teamId.ToString("D"), SubkeyDerivation).PublicKey;

        /// <summary>This node's team-scoped NodeIdentity for a given team (the wire HELLO identity).</summary>
        public NodeIdentity TeamScopedIdentity(Guid teamId) =>
            TeamScopedNodeIdentity.Derive(RootIdentity, teamId.ToString("D"), SubkeyDerivation);
    }

    private NodeFixture NewNode(string name, string teamSeed)
    {
        // DISTINCT root seed per node (the real independent-node case).
        var ed = new Ed25519Signer();
        var (rootPub, rootPriv) = ed.GenerateKeyPair();
        var nodeId = Convert.ToHexString(rootPub.AsSpan(0, 16)).ToLowerInvariant();
        var rootIdentity = new NodeIdentity(nodeId, rootPub, rootPriv);

        var signer = new NodePrincipalSigner(rootPriv);
        // Per-node-distinct party id (gap #2 shape: os:<name>#<key8>).
        var keyHex8 = Convert.ToHexString(signer.Signer.IssuerId.AsSpan()[..4]).ToLowerInvariant();
        var partyId = $"os:{name}#{keyHex8}";

        // A DISTINCT genesis team per node — deterministic from (teamSeed, nodeId) so each node's own team differs.
        var teamId = new Guid(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(teamSeed + ":" + nodeId)).AsSpan(0, 16).ToArray());

        var genesisRoster = MemberRoster.StableGenesis(teamId, partyId, signer.Signer, Verifier);
        var roster = new NodeTeamRoster(genesisRoster);
        var coordinator = new AdmissionCoordinator(Verifier, new InMemoryAdmissionTokenStore(), clock: TimeProvider.System);

        var enrollmentClient = new NodeWireEnrollmentClient(
            rootIdentity, SubkeyDerivation, XWingSubkeyDerivation, signer.Signer, partyId, roster, Verifier, clock: TimeProvider.System);

        var dir = Path.Combine(Path.GetTempPath(), $"harborline-2sided-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHarborlineEntityModule, PeopleEntityModule>();
        services.AddDbContextFactory<LocalNodeDbContext>(opt =>
            opt.UseSqlite($"Data Source={Path.Combine(dir, "local-node.db")};Pooling=False"));
        services.AddSingleton<ICrdtEngine, YDotNetCrdtEngine>(); // real backend — convergence honest.
        var sp = services.BuildServiceProvider();
        var factory = sp.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
        using (var ctx = factory.CreateDbContext()) ctx.Database.EnsureCreated();
        var projection = new ContactCrdtProjection(
            sp.GetRequiredService<ICrdtEngine>(), factory, NullLogger<ContactCrdtProjection>.Instance);
        _cleanup.Add(new ProjectionCleanup(projection, sp));

        return new NodeFixture
        {
            Name = name,
            RootSeed = rootPriv,
            RootIdentity = rootIdentity,
            Signer = signer,
            PartyId = partyId,
            Roster = roster,
            Coordinator = coordinator,
            EnrollmentClient = enrollmentClient,
            Factory = factory,
            Projection = projection,
        };
    }

    /// <summary>
    /// The in-process wire transport that drives A's REAL redeem logic (verify B's signed request → admit over the
    /// invite → build the bootstrap response). This is the EXACT logic <c>AdmissionRoutes.MapRedeemInvite</c> runs,
    /// minus HTTP — so the test exercises the production trust-bootstrap, not a test shortcut.
    /// </summary>
    private sealed class InProcessAdmitterTransport : IEnrollmentTransport
    {
        private readonly NodeFixture _admitter;
        public InProcessAdmitterTransport(NodeFixture admitter) => _admitter = admitter;

        public Task<EnrollmentResponse?> SendAsync(EnrollmentRequest request, CancellationToken ct)
        {
            // (1) A verifies B's signed request (proof-of-possession) — the route's first gate.
            if (!WireEnrollment.VerifyRequest(request, Verifier))
                return Task.FromResult<EnrollmentResponse?>(null);

            // (2) Redeem single-use + admit B (binds B's principal key; no-escalation enforced inside Admit).
            var joiningPrincipal = PrincipalId.FromBase64Url(request.JoiningPrincipalPublicKey);
            var admit = _admitter.Coordinator.AdmitOverInvite(
                _admitter.Roster.Current, request.TokenId, _admitter.PartyId, _admitter.Signer.Signer,
                request.JoiningPartyId, joiningPrincipal, PermissionCompositions.Member);
            if (!admit.Admitted_ || admit.Roster is null)
                return Task.FromResult<EnrollmentResponse?>(null);

            // (3) Wire B's transport key into A's local trust map (gap #3) — A now trusts B's wire HELLO.
            var joiningTransport = PrincipalId.FromBase64Url(request.JoiningTransportPublicKey).AsSpan().ToArray();
            _admitter.Roster.AdmitPeer(admit.Roster, request.JoiningPartyId, joiningTransport);

            // (4) Build the A→B bootstrap response — A's team-scoped transport key (the SAME key A presents on the
            // HELLO) + the per-member transport map (A's own + the just-admitted B).
            var admitterTransportKey = _admitter.TransportKeyForTeam(admit.Roster.TeamId);
            var memberKeys = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                [_admitter.PartyId] = admitterTransportKey,
            };
            foreach (var (party, key) in _admitter.Roster.AdmittedPeerTransportKeys())
                memberKeys[party] = key;

            var response = WireEnrollment.BuildResponse(admit.Roster, admitterTransportKey, memberKeys);
            return Task.FromResult<EnrollmentResponse?>(response);
        }
    }

    // ───────────────────────────── trust-gate + daemon helpers ─────────────────────────

    /// <summary>
    /// Build the EXACT production trust gate (<c>DefaultTeamServiceRegistrar</c> shape): own team subkey (the
    /// never-brick floor) ∪ the live admitted-member transport keys from the node's POST-ENROLLMENT roster. Read
    /// fresh on each handshake (the Func snapshot). This is NOT hand-seeded — it is computed from the roster the
    /// enrollment produced, so the test proves the bootstrap, not a pre-wired set.
    /// </summary>
    private static MemberSetTrustPolicy TrustPolicyFor(byte[] ownTeamSubkey, NodeTeamRoster roster) =>
        new(() =>
        {
            var keys = new List<byte[]> { ownTeamSubkey };
            keys.AddRange(roster.TrustedTransportKeys());
            return keys;
        });

    private static IReadOnlyList<byte[]> UnionFloorAndPeers(byte[] ownTeamSubkey, NodeTeamRoster roster)
    {
        var keys = new List<byte[]> { ownTeamSubkey };
        keys.AddRange(roster.TrustedTransportKeys());
        return keys;
    }

    private GossipDaemon BuildDaemon(
        ISyncDaemonTransport transport,
        NodeIdentity identity,
        IEd25519Signer signer,
        ContactCrdtProjection projection,
        IPeerTrustPolicy trust) =>
        new(
            transport,
            new VectorClock(),
            Options.Create(new GossipDaemonOptions
            {
                RoundIntervalSeconds = 1,
                PeerPickCount = 1,
                ConnectTimeoutSeconds = 5,
                DeadPeerBackoffSeconds = 2,
            }),
            new InMemoryNodeIdentityProvider(identity),
            signer,
            deltaProducer: projection,
            deltaSink: projection,
            trustPolicy: trust, timeProvider: TimeProvider.System);

    private static async Task<Party> CreateContactAsync(NodeFixture r, string displayName)
    {
        var party = Party.Create(LocalTenant, PartyKind.Person, displayName, Actor, new Instant(System.TimeProvider.System.GetUtcNow()));
        await using (var ctx = await r.Factory.CreateDbContextAsync())
        {
            ctx.Set<Party>().Add(party);
            await ctx.SaveChangesAsync();
        }
        r.Projection.ProjectUpsert(party);
        return party;
    }

    private static async Task<string?> ReadDisplayNameAsync(NodeFixture r, PartyId id)
    {
        await using var ctx = await r.Factory.CreateDbContextAsync();
        var p = await ctx.Set<Party>().IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id);
        return p?.DeletedAt is null ? p?.DisplayName : null;
    }

    private static async Task WaitForEfAsync(NodeFixture r, PartyId id, string expected, string because)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        string? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = await ReadDisplayNameAsync(r, id);
            if (last == expected) return;
            await Task.Delay(150);
        }
        Assert.Fail($"{because} Last observed value: '{last ?? "(absent)"}', expected '{expected}'.");
    }

    private sealed class ProjectionCleanup : IAsyncDisposable
    {
        private readonly ContactCrdtProjection _projection;
        private readonly ServiceProvider _sp;
        public ProjectionCleanup(ContactCrdtProjection projection, ServiceProvider sp)
        {
            _projection = projection;
            _sp = sp;
        }
        public async ValueTask DisposeAsync()
        {
            await _projection.DisposeAsync();
            await _sp.DisposeAsync();
        }
    }
}
