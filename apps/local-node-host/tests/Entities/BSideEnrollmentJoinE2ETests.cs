using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Blocks.People.Foundation.Data;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Enrollment;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.Kernel.Crdt;
using Harborline.Api.Kernel.Crdt.Backends;
using Harborline.Api.Kernel.Runtime;
using Harborline.Api.Kernel.Runtime.DependencyInjection;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.Kernel.Security.Crypto;
using Harborline.Api.Kernel.Security.Keys;
using Harborline.Api.Kernel.Sync.Application;
using Harborline.Api.Kernel.Sync.Gossip;
using Harborline.Api.Kernel.Sync.Handshake;
using Harborline.Api.Kernel.Sync.Identity;
using Harborline.Api.Kernel.Sync.Protocol;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.People;
using Harborline.Api.LocalNodeHost.Data.Roster;
using Harborline.Api.LocalNodeHost.Enrollment;
using Harborline.Api.LocalNodeHost.Health;

using Ed25519Signer = Harborline.Api.Kernel.Security.Crypto.Ed25519Signer;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// THE B-SIDE JOIN, END-TO-END, OVER A REAL SOCKET, THROUGH THE PRODUCTION PATH (cerebrum [2026-06-21] —
/// "decisive cross-machine re-verify BLOCKED — B-side join untriggerable"). This is the test the prior socket
/// E2E could NOT be: it drives B's join through the PRODUCTION TRIGGER (<see cref="NodeEnrollmentJoinService"/>),
/// exercises the DAEMON-REBIND in a REAL <see cref="LocalNodeWorker"/>, and uses the REAL production admitter
/// (<see cref="WireEnrollmentAdmitter"/> → <see cref="NodeEnrollmentAdmitter"/>) with its production side-effects
/// (roster-sync publish + SoD audit) — closing BOTH the untriggerable-B-side blocker AND the re-review MINOR
/// (<c>WireEnrollmentOverSocketE2ETests</c> used a <c>TestPreTrustEnrollmentHandler</c> that omitted those).
/// </summary>
/// <remarks>
/// <para>
/// <b>What this proves that nothing before it did.</b>
/// <list type="number">
///   <item><b>B started NORMALLY on its OWN genesis team</b> — a real <see cref="LocalNodeWorker"/> over a real
///     <see cref="TeamContextFactory"/> + <see cref="DefaultTeamServiceRegistrar"/>, its gossip daemon already
///     bound + listening on B's own-team HELLO key (the boot posture).</item>
///   <item><b>B is driven via the PRODUCTION trigger</b> — <see cref="NodeEnrollmentJoinService.JoinAsync"/> (the
///     backend the <c>POST /admission/join</c> route + the Harborline App UI call). It dials A over a REAL
///     <see cref="SocketEnrollmentTransport"/> (TCP, not in-proc), enrolls from the invite alone.</item>
///   <item><b>A is the REAL production admitter</b> — <see cref="NodeEnrollmentAdmitter"/> over
///     <see cref="WireEnrollmentAdmitter"/>, wired into A's gossip daemon's pre-trust enrollment phase, with the
///     production side-effects: it PUBLISHES the admission to a real <see cref="RosterCrdtProjection"/> and RECORDS
///     a real SoD audit event (captured by a recording sink). NOT the test handler.</item>
///   <item><b>B's daemon REBINDS</b> — after <c>SetActiveAsync(A.teamId)</c> the worker stops B's own-team daemon
///     and starts B's A-team daemon. The test asserts B's <see cref="LocalNodeWorker.BoundTeam"/> flipped to
///     A.teamId AND that B now presents its A-team transport key (HKDF(B-root, A.teamId)), not its own-team key.</item>
///   <item><b>The trusted HELLO PASSES from B's side</b> — the <c>PEER_UNTRUSTED</c> that failed cross-machine is
///     RESOLVED: comms cross BOTH ways over the rebound daemons; the roster converges on both nodes.</item>
///   <item><b>A no-invite peer is REJECTED</b> with no leak.</item>
/// </list>
/// </para>
/// <para>
/// A loopback TCP socket is a genuine socket boundary (separate accept/connect, real framing) — the same path a
/// Mac↔Surface dial exercises minus the NIC. The physical cross-machine re-verify is still HW-gated, but the
/// transport + the full B-side production path are now proven over a real socket.
/// </para>
/// </remarks>
public sealed class BSideEnrollmentJoinE2ETests : IAsyncLifetime
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

    [Fact(DisplayName = "B-side JOIN end-to-end over a REAL SOCKET via the PRODUCTION trigger: B (real worker, own team, daemon bound) joins A via NodeEnrollmentJoinService → real admitter side-effects run (roster-sync publish + SoD audit) → B's daemon REBINDS to the A-team key → trusted HELLO passes, comms cross both ways, roster converges, no-invite peer rejected")]
    public async Task BSideJoin_ProductionTrigger_RealSocket_DaemonRebinds_HandshakePasses_RealAdmitterSideEffects()
    {
        // ── A: an INDEPENDENT admitter node. Its own root, genesis team, roster, real admission seam. ───────────
        var a = NewNode("A", teamSeed: "team-A-office");
        // ── B: an INDEPENDENT joiner node. DISTINCT root, DISTINCT own-team, DISTINCT genesis. ──────────────────
        var b = await NewWorkerNodeAsync("B", teamSeed: "team-B-home");

        Assert.NotEqual(a.Roster.Current.TeamId, b.Node.Roster.Current.TeamId);

        // B booted on its OWN team — its worker-driven daemon is bound + listening on B's own-team HELLO key.
        Assert.NotNull(b.Worker.BoundGossip);
        Assert.Equal(b.Node.GenesisTeamId, b.Worker.BoundTeam!.TeamId.Value);
        var bOwnTeamKey = b.Node.TransportKeyForTeam(b.Node.GenesisTeamId);
        var bATeamKey = b.Node.TransportKeyForTeam(a.Roster.Current.TeamId);
        Assert.False(bOwnTeamKey.AsSpan().SequenceEqual(bATeamKey),
            "B's own-team key and its A-team key must differ (HKDF on distinct team ids) — the whole point of rebind.");

        // ── A mints a REAL invite + stands up its REAL network enrollment listener (production admitter). ───────
        var anchor = TeamTrustAnchor.FromRoster(a.Roster.Current);
        var invite = a.Coordinator.CreateInvite(anchor);

        var aDeviceForEnroll = a.TeamScopedIdentity(a.Roster.Current.TeamId);
        var aEnrollTransport = new TcpSyncDaemonTransport("tcp://127.0.0.1:0");
        _cleanup.Add(aEnrollTransport);
        var aEnrollEndpoint = aEnrollTransport.ListenEndpoint!;
        // PRODUCTION admitter: NodeEnrollmentAdmitter → WireEnrollmentAdmitter (real roster-sync publish + SoD audit).
        var aEnrollDaemon = BuildDaemonWithEnrollment(
            aEnrollTransport, aDeviceForEnroll, a.TransportSigner, a.Projection,
            TrustPolicyFor(aDeviceForEnroll.PublicKey, a.Roster),
            a.ProductionEnrollmentHandler);
        _cleanup.Add(aEnrollDaemon);
        await aEnrollDaemon.StartListeningAsync(CancellationToken.None);

        // ── B JOINS via the PRODUCTION TRIGGER. NodeEnrollmentJoinService.JoinAsync → EnrollAsync over the real
        //    socket → AdoptEnrollment → materialize+activate A.teamId → SetActiveAsync(A.teamId) → DAEMON REBINDS. ─
        b.SetAdmitterSyncEndpoint(aEnrollEndpoint);
        var joinOutcome = await b.JoinService.JoinAsync(invite.TokenId, anchor, CancellationToken.None);

        Assert.True(joinOutcome.Succeeded,
            $"B must JOIN over the socket via the production trigger. Reason: {joinOutcome.FailureReason}");
        Assert.Equal(a.Roster.Current.TeamId, joinOutcome.TeamId);

        await aEnrollDaemon.StopListeningAsync(CancellationToken.None);

        // ── DAEMON-REBIND ASSERTED: B's worker now drives the A-team daemon, presenting B's A-team HELLO key. ───
        // Wait for the worker's async rebind (fired off the SetActiveAsync ActiveChanged event) to settle.
        await WaitUntilAsync(
            () => b.Worker.BoundTeam is not null
                && b.Worker.BoundTeam.TeamId.Value == a.Roster.Current.TeamId,
            because: "B's worker must REBIND its gossip daemon to A's team after the join's active-team switch.",
            faultProbe: () => b.Worker.LastRebind.IsHealthy ? null : b.Worker.LastRebind.FaultSummary);
        Assert.Equal(a.Roster.Current.TeamId, b.Worker.BoundTeam!.TeamId.Value);

        // The rebound daemon's identity provider derives B's A-team subkey — NOT B's own-team key.
        var bReboundIdentity = b.Worker.BoundTeam!.Services.GetRequiredService<INodeIdentityProvider>().Current;
        Assert.True(bReboundIdentity.PublicKey.AsSpan().SequenceEqual(bATeamKey),
            "the REBOUND daemon must present B's A-team-scoped HELLO key (HKDF(B-root, A.teamId)) — the key A "
            + "recorded at admission — NOT B's own-team key (which would fail PEER_UNTRUSTED from B's side).");
        Assert.False(bReboundIdentity.PublicKey.AsSpan().SequenceEqual(bOwnTeamKey),
            "the rebound daemon must NOT still present B's own-team key.");

        // ── REAL ADMITTER SIDE-EFFECTS ran (closes the re-review MINOR). ─────────────────────────────────────────
        Assert.True(a.EnrollmentControlAudit.MemberAdmittedCount >= 1,
            "the PRODUCTION admitter must have recorded a SoD member-admitted audit event (the second-set-of-eyes "
            + "control) — the in-proc/test-handler tests omitted this.");
        Assert.Contains(a.EnrollmentControlAudit.AdmittedParties, p => string.Equals(p, b.Node.PartyId, StringComparison.Ordinal));
        // The PRODUCTION admit core PUBLISHED B's admission to the roster-sync doctype (the real RosterCrdtProjection,
        // not a stub). The fixture does not seed A's genesis self-admission record into the projection (the
        // bootstrap hosted service does that in production), so the count reflects B's admission alone — ≥1 proves
        // the publish side-effect ran (what the test-handler tests omitted).
        Assert.True(a.RosterProjection.Count >= 1,
            "the PRODUCTION admitter must have PUBLISHED B's admission to the roster-sync doctype.");
        Assert.Contains(a.RosterProjection.Snapshot(),
            r => string.Equals(r.PartyId, b.Node.PartyId, StringComparison.Ordinal));

        // ── MUTUAL TRUST: both trust sets hold BOTH A-team transport keys (computed from post-join rosters). ────
        var aTransportKey = a.TransportKeyForTeam(a.Roster.Current.TeamId);
        var aTrustKeys = UnionFloorAndPeers(aTransportKey, a.Roster);
        Assert.Contains(aTrustKeys, k => k.AsSpan().SequenceEqual(aTransportKey));   // A trusts itself.
        Assert.Contains(aTrustKeys, k => k.AsSpan().SequenceEqual(bATeamKey));       // A trusts B.

        var bTrustKeys = UnionFloorAndPeers(bATeamKey, b.Node.Roster);
        Assert.Contains(bTrustKeys, k => k.AsSpan().SequenceEqual(bATeamKey));       // B trusts itself.
        Assert.Contains(bTrustKeys, k => k.AsSpan().SequenceEqual(aTransportKey));   // B trusts A.

        // ── ROSTER CONVERGED on BOTH nodes, validates to A's genesis. ─────────────────────────────────────────
        Assert.True(a.Roster.Current.Contains(b.Node.PartyId), "A's roster must contain B (A admitted B over the socket).");
        Assert.True(a.Roster.Current.ValidatesToGenesis(Verifier));
        Assert.True(b.Node.Roster.Current.Contains(b.Node.PartyId), "B's adopted roster must contain B.");
        var expectedXWingKey =
            b.Node.EnrollmentClient.DeriveXWingPublicKeyForTeam(a.Roster.Current.TeamId.ToString("D"));
        Assert.True(a.Roster.Current.XWingPublicKeyOf(b.Node.PartyId)!.AsSpan().SequenceEqual(expectedXWingKey));
        Assert.True(b.Node.Roster.Current.XWingPublicKeyOf(b.Node.PartyId)!.AsSpan().SequenceEqual(expectedXWingKey));
        Assert.True(b.Node.Roster.Current.Contains(a.PartyId), "B's adopted roster must contain A (the admitter).");
        Assert.True(b.Node.Roster.Current.ValidatesToGenesis(Verifier), "B's adopted roster must validate to A's genesis.");
        Assert.Equal(a.PartyId, b.Node.Roster.Current.GenesisPartyId);

        // ── COMMS CROSS BOTH WAYS over the rebound daemons (the trusted HELLO this join bootstrapped). ──────────
        // Use the REBOUND B daemon (the worker's) on one direction + a fresh A daemon — proving B's worker-managed,
        // rebound daemon actually carries traffic on the A-team key. The rebound B daemon is listening already.
        var deviceA = a.TeamScopedIdentity(a.Roster.Current.TeamId);
        var bReboundDaemon = (GossipDaemon)b.Worker.BoundGossip!;
        var bReboundListenEndpoint = b.Worker.BoundTeam!.Services
            .GetRequiredService<ISyncDaemonTransport>() is TcpSyncDaemonTransport bTcp
                ? bTcp.ListenEndpoint
                : null;
        Assert.NotNull(bReboundListenEndpoint);

        // A dials B's REBOUND listener.
        var transportAOut = new TcpSyncDaemonTransport();
        _cleanup.Add(transportAOut);
        var daemonA = BuildDaemon(transportAOut, deviceA, a.TransportSigner, a.Projection, TrustPolicyFor(deviceA.PublicKey, a.Roster));
        _cleanup.Add(daemonA);
        daemonA.AddPeer(bReboundListenEndpoint!, bATeamKey);
        await daemonA.StartAsync(CancellationToken.None);

        var fromA = await CreateContactAsync(a, "Ada Lovelace");
        await WaitForEfAsync(b.Node, fromA.Id, expected: "Ada Lovelace",
            because: "after the production-trigger JOIN + daemon-rebind, A→B comms must cross the trusted session "
                + "to B's REBOUND daemon (the bootstrap that never worked cross-machine before).");

        // B→A also crosses (mutual): A listens, B's rebound daemon dials A.
        var transportAListen = new TcpSyncDaemonTransport("tcp://127.0.0.1:0");
        _cleanup.Add(transportAListen);
        var endpointA = transportAListen.ListenEndpoint!;
        var daemonAListen = BuildDaemon(transportAListen, deviceA, a.TransportSigner, a.Projection, TrustPolicyFor(deviceA.PublicKey, a.Roster));
        _cleanup.Add(daemonAListen);
        await daemonAListen.StartListeningAsync(CancellationToken.None);

        bReboundDaemon.AddPeer(endpointA, aTransportKey);
        await bReboundDaemon.TriggerPushAsync(OutboundSyncLane.Background, CancellationToken.None);

        var fromB = await CreateContactAsync(b.Node, "Grace Hopper");
        await bReboundDaemon.TriggerPushAsync(OutboundSyncLane.Foreground, CancellationToken.None);
        await WaitForEfAsync(a, fromB.Id, expected: "Grace Hopper",
            because: "after the JOIN, B→A comms must also cross from B's REBOUND daemon (mutual trust on the A-team key).");

        await daemonA.StopAsync(CancellationToken.None);
        await daemonAListen.StopListeningAsync(CancellationToken.None);

        // ── A NO-INVITE remote peer that DIALS the production enrollment listener is REJECTED with NO leak. ─────
        var aEnrollTransport2 = new TcpSyncDaemonTransport("tcp://127.0.0.1:0");
        _cleanup.Add(aEnrollTransport2);
        var aEnrollEndpoint2 = aEnrollTransport2.ListenEndpoint!;
        var aEnrollDaemon2 = BuildDaemonWithEnrollment(
            aEnrollTransport2, aDeviceForEnroll, a.TransportSigner, a.Projection,
            TrustPolicyFor(aDeviceForEnroll.PublicKey, a.Roster),
            a.ProductionEnrollmentHandler);
        _cleanup.Add(aEnrollDaemon2);
        await aEnrollDaemon2.StartListeningAsync(CancellationToken.None);

        var charlie = NewNode("Charlie", teamSeed: "team-A-office");
        var charlieSocket = new SocketEnrollmentTransport(
            admitterEndpoint: () => aEnrollEndpoint2,
            timeout: TimeSpan.FromSeconds(20),
            logger: NullLogger<SocketEnrollmentTransport>.Instance);
        var charlieOutcome = await charlie.EnrollmentClient.EnrollAsync(
            tokenId: Guid.NewGuid().ToString("N"),       // a token A never minted → invite_rejected.
            inviteAnchor: anchor,
            transport: charlieSocket,
            CancellationToken.None);

        Assert.False(charlieOutcome.Succeeded, "a no-invite remote peer must be REJECTED over the socket.");
        Assert.Empty(charlie.Roster.TrustedTransportKeys());
        Assert.False(a.Roster.Current.Contains(charlie.PartyId), "A must NOT have admitted the no-invite peer.");

        await aEnrollDaemon2.StopListeningAsync(CancellationToken.None);

        // ── SINGLE-USER UNCHANGED: a node that never joins keeps its boot team bound (no rebind path entered). ──
        Assert.Equal(a.Roster.Current.TeamId, b.Worker.BoundTeam!.TeamId.Value); // B switched (it joined) — expected.
        // (The single-user invariant is structurally guaranteed: OnActiveTeamChanged only fires on a switch; a node
        //  that never calls SetActiveAsync beyond bootstrap stays on its boot team — exercised by LocalNodeWorkerTests.)
    }

    // ── THE FIXED-PORT REBIND TEST (cerebrum [2026-06-21] verdict BLOCKER-1). ─────────────────────────────────
    //
    // This is the test the ORIGINAL E2E could not be: it binds B's per-team transports on the PRODUCTION FIXED
    // PORT (a fixed tcp://127.0.0.1:<P>, NOT the ephemeral tcp://127.0.0.1:0 the shipping config uses) — the exact
    // config gap-C injects (BindAddress=0.0.0.0:7473 as the stable dial target). Both of B's per-team transports
    // (own-team + the joined A-team) bind the SAME fixed P, because listenBindEndpoint is a single closure value.
    //
    // PRE-FIX this DEADLOCKS the rebind: B's own-team TcpSyncDaemonTransport holds P (bound in its ctor, released
    // only on DisposeAsync); the rebind stopped the old daemon's LOOPS but never disposed the old context, so the
    // A-team transport's ctor hit `SocketException EADDRINUSE` binding the SAME P → the rebind faulted → B's
    // BoundTeam never flipped to A. POST-FIX StopBoundGossipAsync disposes the old context FIRST (releasing P), so
    // the A-team transport binds the SAME P cleanly → BoundTeam flips → the trusted HELLO passes → comms cross.
    //
    // Two nodes on one host can't co-exist on one fixed port, so ONLY B uses the fixed P; A (a separate node — a
    // separate machine in production) uses its own ephemeral enrollment listener. The point the verdict makes is
    // that B's SINGLE-node rebind must FREE + REBIND the SAME fixed port — which is precisely what this exercises.
    [Fact(DisplayName = "B-side JOIN on the PRODUCTION FIXED PORT (not ephemeral): B's own-team daemon holds the fixed port; the rebind must DISPOSE the old transport to FREE it before the A-team daemon binds the SAME port — pre-fix EADDRINUSE / post-fix the rebind settles, BoundTeam flips, comms cross (verdict BLOCKER-1)")]
    public async Task BSideJoin_FixedPort_RebindFreesAndRebindsSamePort_NoAddressInUse()
    {
        // A — a SEPARATE node (separate machine in prod): its own ephemeral enrollment listener, no shared port.
        var a = NewNode("A", teamSeed: "team-A-office");

        // B — bound to a FIXED loopback port (the production fixed-port shape, not ephemeral). BOTH of B's per-team
        // transports (own-team now, A-team after the join) resolve THIS one endpoint from the registrar closure.
        // GrabFreePort releases the port before NewWorkerNodeAsync's host-start re-binds it — a TOCTOU window in
        // which one of this project's ~40 parallel ephemeral-binding E2E classes can be handed the same port and
        // hold it for its whole run (longer than the transport's transient-bind-retry budget). That is a TEST-only
        // artifact (production has no second listener fighting for 7473), so retry the WHOLE boot on a fresh port.
        var (b, fixedBind) = await BootBOnAFreeFixedPortAsync();

        // B booted on its own team, its daemon BOUND + LISTENING on the FIXED port.
        Assert.NotNull(b.Worker.BoundGossip);
        Assert.Equal(b.Node.GenesisTeamId, b.Worker.BoundTeam!.TeamId.Value);
        var bOwnListen = b.Worker.BoundTeam!.Services.GetRequiredService<ISyncDaemonTransport>()
            is TcpSyncDaemonTransport ownTcp ? ownTcp.ListenEndpoint : null;
        Assert.Equal(fixedBind, bOwnListen); // a FIXED port — no ephemeral :0 resolution, the production shape.

        var bATeamKey = b.Node.TransportKeyForTeam(a.Roster.Current.TeamId);

        // A's REAL production enrollment listener (its own ephemeral port — a separate node).
        var anchor = TeamTrustAnchor.FromRoster(a.Roster.Current);
        var invite = a.Coordinator.CreateInvite(anchor);
        var aDeviceForEnroll = a.TeamScopedIdentity(a.Roster.Current.TeamId);
        var aEnrollTransport = new TcpSyncDaemonTransport("tcp://127.0.0.1:0");
        _cleanup.Add(aEnrollTransport);
        var aEnrollEndpoint = aEnrollTransport.ListenEndpoint!;
        var aEnrollDaemon = BuildDaemonWithEnrollment(
            aEnrollTransport, aDeviceForEnroll, a.TransportSigner, a.Projection,
            TrustPolicyFor(aDeviceForEnroll.PublicKey, a.Roster), a.ProductionEnrollmentHandler);
        _cleanup.Add(aEnrollDaemon);
        await aEnrollDaemon.StartListeningAsync(CancellationToken.None);

        // B JOINS via the production trigger — the rebind must free the fixed port (dispose old) then bind it (new).
        b.SetAdmitterSyncEndpoint(aEnrollEndpoint);
        var joinOutcome = await b.JoinService.JoinAsync(invite.TokenId, anchor, CancellationToken.None);
        Assert.True(joinOutcome.Succeeded, $"B must JOIN. Reason: {joinOutcome.FailureReason}");

        await aEnrollDaemon.StopListeningAsync(CancellationToken.None);

        // THE PROOF (BLOCKER-1): the rebind SETTLED — BoundTeam flipped to A. Pre-fix this never happens (the A-team
        // transport ctor throws EADDRINUSE binding the fixed port the un-disposed own-team transport still holds),
        // and the worker's fail-loud path records a FAULT (LastRebind.IsHealthy=false) — so this assert fails.
        await WaitUntilAsync(
            () => b.Worker.BoundTeam is not null && b.Worker.BoundTeam.TeamId.Value == a.Roster.Current.TeamId,
            because: "the rebind must FREE the fixed port (dispose the old transport) and REBIND the SAME port for "
                + "the A-team daemon — pre-fix it faults EADDRINUSE and BoundTeam never flips.",
            faultProbe: () => b.Worker.LastRebind.IsHealthy ? null : b.Worker.LastRebind.FaultSummary);
        Assert.Equal(a.Roster.Current.TeamId, b.Worker.BoundTeam!.TeamId.Value);

        // The rebind reported HEALTHY (no fault) — the fail-loud signal must NOT be set after a successful rebind.
        Assert.True(b.Worker.LastRebind.IsHealthy,
            $"a successful rebind must report healthy; got fault: {b.Worker.LastRebind.FaultSummary}");

        // The A-team daemon is bound on the SAME FIXED PORT B's own-team daemon previously held (freed + rebound).
        var bReboundListen = b.Worker.BoundTeam!.Services.GetRequiredService<ISyncDaemonTransport>()
            is TcpSyncDaemonTransport reboundTcp ? reboundTcp.ListenEndpoint : null;
        Assert.Equal(fixedBind, bReboundListen);
        Assert.Equal(bOwnListen, bReboundListen); // SAME fixed port — freed by dispose, rebound by the new team.

        // The rebound daemon presents B's A-team key (the handshake key A recorded) — the rebind actually re-keyed.
        var bReboundIdentity = b.Worker.BoundTeam!.Services.GetRequiredService<INodeIdentityProvider>().Current;
        Assert.True(bReboundIdentity.PublicKey.AsSpan().SequenceEqual(bATeamKey),
            "the rebound daemon (on the freed-and-rebound fixed port) must present B's A-team HELLO key.");

        // And comms CROSS to B's rebound daemon on the fixed port — A dials B's fixed-port listener and a contact
        // converges, proving the rebound listener on the reused port actually serves the trusted session.
        var deviceA = a.TeamScopedIdentity(a.Roster.Current.TeamId);
        var transportAOut = new TcpSyncDaemonTransport();
        _cleanup.Add(transportAOut);
        var daemonA = BuildDaemon(transportAOut, deviceA, a.TransportSigner, a.Projection, TrustPolicyFor(deviceA.PublicKey, a.Roster));
        _cleanup.Add(daemonA);
        daemonA.AddPeer(bReboundListen!, bATeamKey);
        await daemonA.StartAsync(CancellationToken.None);

        var fromA = await CreateContactAsync(a, "Edsger Dijkstra");
        await WaitForEfAsync(b.Node, fromA.Id, expected: "Edsger Dijkstra",
            because: "after the fixed-port rebind, A→B comms must cross to B's daemon REBOUND on the SAME fixed port "
                + "(the freed-and-rebound listener serves the trusted session).");

        await daemonA.StopAsync(CancellationToken.None);
    }

    /// <summary>Grab a free loopback TCP port number (bind ephemeral, read the OS-assigned port, release). Tiny
    /// TOCTOU window — acceptable for a test that needs a FIXED port to exercise the production fixed-port rebind
    /// (the shipping config uses a fixed 7473, not the ephemeral :0 the original E2E used — the verdict's point).</summary>
    private static int GrabFreePort()
    {
        using var probe = new System.Net.Sockets.Socket(
            System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Stream,
            System.Net.Sockets.ProtocolType.Tcp);
        probe.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        return ((System.Net.IPEndPoint)probe.LocalEndPoint!).Port;
    }

    /// <summary>
    /// Boot B on a FIXED loopback port, retrying on a fresh port if the boot bind collides. Closes the
    /// <see cref="GrabFreePort"/>-to-host-start TOCTOU window: this project runs ~40 ephemeral-socket E2E
    /// classes in parallel, any of which can be handed the just-released probe port and hold it for its
    /// whole run — outliving the transport's transient-bind-retry budget. That contention is a TEST artifact
    /// (production has exactly one process binding the fixed 7473; nothing competes), so on the rare boot
    /// collision we simply pick another free port and re-boot. The retry is bounded; a persistent inability
    /// to bind ANY of several fresh ports is a real environment fault and surfaces as the last bind throw.
    /// </summary>
    private async Task<(WorkerNode Worker, string FixedBind)> BootBOnAFreeFixedPortAsync()
    {
        const int maxAttempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            var fixedBind = $"tcp://127.0.0.1:{GrabFreePort()}";
            try
            {
                var b = await NewWorkerNodeAsync("B", teamSeed: "team-B-home", listenBindEndpoint: fixedBind);
                return (b, fixedBind);
            }
            catch (InvalidOperationException ex) when (
                attempt < maxAttempts && ex.Message.Contains("already held", StringComparison.Ordinal))
            {
                // The grabbed port was taken in the TOCTOU window by a parallel test's ephemeral bind. Pick
                // a fresh one and re-boot — the fixed-port rebind path under test is unaffected by WHICH
                // fixed port B uses, only that it is a single stable port both of B's transports resolve.
            }
        }
    }

    // ─────────────────────────────── A: a self-contained production-admitter node ────────────────────────────

    private sealed class AdmitterNode
    {
        public required string Name { get; init; }
        public required NodeIdentity RootIdentity { get; init; }
        public required IEd25519Signer TransportSigner { get; init; }
        public required string PartyId { get; init; }
        public required NodeTeamRoster Roster { get; init; }
        public required AdmissionCoordinator Coordinator { get; init; }
        public required NodeWireEnrollmentClient EnrollmentClient { get; init; }
        public required IDbContextFactory<LocalNodeDbContext> Factory { get; init; }
        public required ContactCrdtProjection Projection { get; init; }
        public required RosterCrdtProjection RosterProjection { get; init; }
        public required RecordingEnrollmentControlRecorder EnrollmentControlAudit { get; init; }
        public required IPreTrustEnrollmentHandler ProductionEnrollmentHandler { get; init; }
        public required IActiveTeamAccessor ActiveTeam { get; init; }

        public byte[] TransportKeyForTeam(Guid teamId) =>
            TeamScopedNodeIdentity.Derive(RootIdentity, teamId.ToString("D"), SubkeyDerivation).PublicKey;

        public NodeIdentity TeamScopedIdentity(Guid teamId) =>
            TeamScopedNodeIdentity.Derive(RootIdentity, teamId.ToString("D"), SubkeyDerivation);
    }

    /// <summary>
    /// Build A — an admitter node with the FULL PRODUCTION admit core: a real <see cref="RosterCrdtProjection"/>
    /// (so the admit PUBLISHES to roster-sync), a recording <see cref="IEnrollmentCompensatingControlRecorder"/> (so the admit RECORDS the
    /// SoD audit), and the production <see cref="NodeEnrollmentAdmitter"/> → <see cref="WireEnrollmentAdmitter"/>
    /// wired as the enrollment handler. A single-team active accessor backs the admitter's team-scoped transport
    /// key lookup (the #1296-F2 source of truth).
    /// </summary>
    private AdmitterNode NewNode(string name, string teamSeed)
    {
        var ed = new Ed25519Signer();
        var (rootPub, rootPriv) = ed.GenerateKeyPair();
        var nodeId = Convert.ToHexString(rootPub.AsSpan(0, 16)).ToLowerInvariant();
        var rootIdentity = new NodeIdentity(nodeId, rootPub, rootPriv);

        var signer = new NodePrincipalSigner(rootPriv);
        var keyHex8 = Convert.ToHexString(signer.Signer.IssuerId.AsSpan()[..4]).ToLowerInvariant();
        var partyId = $"os:{name}#{keyHex8}";

        var teamId = new Guid(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(teamSeed + ":" + nodeId)).AsSpan(0, 16).ToArray());

        var genesisRoster = MemberRoster.StableGenesis(teamId, partyId, signer.Signer, Verifier);
        var roster = new NodeTeamRoster(genesisRoster);
        var coordinator = new AdmissionCoordinator(Verifier, new InMemoryAdmissionTokenStore(), clock: TimeProvider.System);

        var enrollmentClient = new NodeWireEnrollmentClient(
            rootIdentity, SubkeyDerivation, XWingSubkeyDerivation, signer.Signer, partyId, roster, Verifier, clock: TimeProvider.System);

        // Per-node SQLite (contacts) + a roster CRDT projection (the real roster-sync publish target).
        var dir = NewTempDir($"bside-A-{name}");
        var sp = BuildNodeServices(dir, roster);
        var factory = sp.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
        using (var ctx = factory.CreateDbContext()) ctx.Database.EnsureCreated();
        var projection = new ContactCrdtProjection(
            sp.GetRequiredService<ICrdtEngine>(), factory, NullLogger<ContactCrdtProjection>.Instance);
        _cleanup.Add(new ProjectionCleanup(projection, sp));

        var rosterDir = NewTempDir($"bside-A-roster-{name}");
        var rosterSp = BuildRosterServices(rosterDir);
        var rosterFactory = rosterSp.GetRequiredService<IDbContextFactory<NodeLocalRosterDbContext>>();
        using (var rctx = rosterFactory.CreateDbContext()) rctx.Database.EnsureCreated();
        var rosterProjection = new RosterCrdtProjection(TimeProvider.System,
            rosterSp.GetRequiredService<ICrdtEngine>(), rosterFactory, Verifier, signer.Signer,
            NullLogger<RosterCrdtProjection>.Instance, roster);
        _cleanup.Add(new RosterProjectionCleanup(rosterProjection, rosterSp));

        var enrollmentControlAudit = new RecordingEnrollmentControlRecorder();
        var activeTeam = new SingleTeamActiveAccessor(teamId, rootIdentity);

        var wireAdmitter = new WireEnrollmentAdmitter(
            coordinator, roster, signer.Signer, partyId, Verifier, rosterProjection, enrollmentControlAudit, activeTeam);
        var prodHandler = new NodeEnrollmentAdmitter(
            wireAdmitter,
            PlainEnrollmentRedeemDispatch.Instance,
            NullLogger<NodeEnrollmentAdmitter>.Instance);

        return new AdmitterNode
        {
            Name = name,
            RootIdentity = rootIdentity,
            TransportSigner = ed,
            PartyId = partyId,
            Roster = roster,
            Coordinator = coordinator,
            EnrollmentClient = enrollmentClient,
            Factory = factory,
            Projection = projection,
            RosterProjection = rosterProjection,
            EnrollmentControlAudit = enrollmentControlAudit,
            ProductionEnrollmentHandler = prodHandler,
            ActiveTeam = activeTeam,
        };
    }

    private sealed class PlainEnrollmentRedeemDispatch : IEnrollmentRedeemDispatch
    {
        internal static PlainEnrollmentRedeemDispatch Instance { get; } = new();

        public Task<PairingDispatchOutcome> DispatchAsync(
            EnrollmentRequest request,
            string source,
            CancellationToken ct) =>
            Task.FromResult(PairingDispatchOutcome.PlainPath());
    }

    // ─────────────────────────────── B: a REAL worker node (the daemon-rebind subject) ──────────────────────

    private sealed class WorkerNode
    {
        public required BNode Node { get; init; }
        public required LocalNodeWorker Worker { get; init; }
        public required IHost Host { get; init; }
        public required EnrollmentEndpointHolder EndpointHolder { get; init; }
        public required NodeEnrollmentJoinService JoinService { get; init; }

        public void SetAdmitterSyncEndpoint(string endpoint) => EndpointHolder.Endpoint = endpoint;
    }

    private sealed class BNode
    {
        public required NodeIdentity RootIdentity { get; init; }
        public required string PartyId { get; init; }
        public required NodeTeamRoster Roster { get; init; }
        public required NodeWireEnrollmentClient EnrollmentClient { get; init; }
        public required Guid GenesisTeamId { get; init; }
        public required IDbContextFactory<LocalNodeDbContext> Factory { get; init; }
        public required ContactCrdtProjection Projection { get; init; }

        public byte[] TransportKeyForTeam(Guid teamId) =>
            TeamScopedNodeIdentity.Derive(RootIdentity, teamId.ToString("D"), SubkeyDerivation).PublicKey;
    }

    /// <summary>A mutable holder so the wired <see cref="SocketEnrollmentTransport"/> reads the admitter endpoint
    /// AT CALL TIME (set once A's listener is up), exactly as production reads it from config per-call.</summary>
    private sealed class EnrollmentEndpointHolder
    {
        public string? Endpoint { get; set; }
    }

    /// <summary>
    /// Build B as a REAL <see cref="LocalNodeWorker"/> over a real <see cref="TeamContextFactory"/> +
    /// <see cref="DefaultTeamServiceRegistrar"/> (listenForPeers=true so the daemon binds a TCP listener), bootstrap
    /// its OWN genesis team active, and start the host — so B's gossip daemon is bound + listening on B's own-team
    /// key BEFORE the join (the boot posture the rebind must change). The join service + a socket transport reading
    /// the endpoint holder are wired exactly as production wires them. A no-op store activator stands in for the
    /// SQLCipher store (the gossip/enroll path does not read it — keeps the test off native SQLCipher).
    /// </summary>
    private Task<WorkerNode> NewWorkerNodeAsync(string name, string teamSeed)
        => NewWorkerNodeAsync(name, teamSeed, listenBindEndpoint: "tcp://127.0.0.1:0");

    private async Task<WorkerNode> NewWorkerNodeAsync(string name, string teamSeed, string listenBindEndpoint)
    {
        var ed = new Ed25519Signer();
        var (rootPub, rootPriv) = ed.GenerateKeyPair();
        var nodeId = Convert.ToHexString(rootPub.AsSpan(0, 16)).ToLowerInvariant();
        var rootIdentity = new NodeIdentity(nodeId, rootPub, rootPriv);

        var signer = new NodePrincipalSigner(rootPriv);
        var keyHex8 = Convert.ToHexString(signer.Signer.IssuerId.AsSpan()[..4]).ToLowerInvariant();
        var partyId = $"os:{name}#{keyHex8}";

        var genesisTeamId = new Guid(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(teamSeed + ":" + nodeId)).AsSpan(0, 16).ToArray());

        var genesisRoster = MemberRoster.StableGenesis(genesisTeamId, partyId, signer.Signer, Verifier);
        var roster = new NodeTeamRoster(genesisRoster);

        var enrollmentClient = new NodeWireEnrollmentClient(
            rootIdentity, SubkeyDerivation, XWingSubkeyDerivation, signer.Signer, partyId, roster, Verifier, clock: TimeProvider.System);

        // B's contacts SQLite + projection (the synced doctype the daemon ships).
        var dir = NewTempDir($"bside-B-{name}");
        var sp = BuildNodeServices(dir, roster);
        var factory = sp.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
        using (var ctx = factory.CreateDbContext()) ctx.Database.EnsureCreated();
        var projection = new ContactCrdtProjection(
            sp.GetRequiredService<ICrdtEngine>(), factory, NullLogger<ContactCrdtProjection>.Instance);
        _cleanup.Add(new ProjectionCleanup(projection, sp));

        var bNode = new BNode
        {
            RootIdentity = rootIdentity,
            PartyId = partyId,
            Roster = roster,
            EnrollmentClient = enrollmentClient,
            GenesisTeamId = genesisTeamId,
            Factory = factory,
            Projection = projection,
        };

        // The OUTER host container for B: it must register the per-team registrar's dependencies —
        //   * IDeltaProducer/IDeltaSink (the contacts projection, so the per-team daemon ships contacts),
        //   * ITrustedMemberKeyProvider (B's NodeTeamRoster, so the trust policy reads B's adopted roster),
        //   * IPreTrustEnrollmentHandler (B can ALSO admit — symmetric; harmless here).
        var dataDir = NewTempDir($"bside-B-data-{name}");
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddTestKernelClock();
        builder.Services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        builder.Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        builder.Services.AddHarborlineKernelRuntime();
        // The contacts delta router (the per-team daemon bridges to it via the outer provider).
        builder.Services.AddSingleton<IDeltaProducer>(projection);
        builder.Services.AddSingleton<IDeltaSink>(projection);
        // B's roster as the trusted-member key provider — so the per-team MemberSetTrustPolicy unions B's adopted
        // peers' transport keys (A's key, after the join) with B's own-team floor.
        builder.Services.AddSingleton<ITrustedMemberKeyProvider>(roster);

        // The per-team registrar — listenForPeers=true so the daemon binds a real TCP listener.
        builder.Services.AddHarborlineMultiTeam(DefaultTeamServiceRegistrar.Compose(
            dataDirectory: dataDir,
            subkeyDerivation: SubkeyDerivation,
            rootIdentity: rootIdentity,
            sqlCipherKeyDerivation: new SqlCipherKeyDerivation(),
            listenForPeers: true,
            listenBindEndpoint: listenBindEndpoint,
            roundIntervalSeconds: 1));

        // A no-op store activator (the gossip/enroll path does not read the encrypted store; keeps the test off
        // native SQLCipher). The join service calls ActivateAsync — this satisfies that without opening a store.
        builder.Services.AddSingleton<ITeamStoreActivator, NoopStoreActivator>();

        // The join service + the socket transport (reads the admitter endpoint from the holder at call time).
        var endpointHolder = new EnrollmentEndpointHolder();
        IEnrollmentTransport transport = new SocketEnrollmentTransport(
            admitterEndpoint: () => endpointHolder.Endpoint
                ?? throw new InvalidOperationException("admitter endpoint not set"),
            timeout: TimeSpan.FromSeconds(20),
            logger: NullLogger<SocketEnrollmentTransport>.Instance);
        builder.Services.AddSingleton(transport);
        builder.Services.AddSingleton(enrollmentClient);
        builder.Services.AddSingleton(sp2 => new NodeEnrollmentJoinService(
            client: sp2.GetRequiredService<NodeWireEnrollmentClient>(),
            transport: sp2.GetRequiredService<IEnrollmentTransport>(),
            teamContextFactory: sp2.GetRequiredService<ITeamContextFactory>(),
            storeActivator: sp2.GetRequiredService<ITeamStoreActivator>(),
            activeTeam: sp2.GetRequiredService<IActiveTeamAccessor>(),
            logger: NullLogger<NodeEnrollmentJoinService>.Instance));

        builder.Services.AddSingleton(projection); // ContactCrdtProjection for the worker's push-on-change.
        builder.Services.AddHostedService<LocalNodeWorker>();
        builder.Services.AddSingleton(sp2 => (LocalNodeWorker)sp2.GetServices<IHostedService>()
            .First(s => s is LocalNodeWorker));

        var host = builder.Build();

        // Bootstrap B's OWN genesis team active BEFORE starting the worker (mirrors MultiTeamBootstrapHostedService).
        var teamFactory = host.Services.GetRequiredService<ITeamContextFactory>();
        var activeTeam = host.Services.GetRequiredService<IActiveTeamAccessor>();
        await teamFactory.GetOrCreateAsync(new TeamId(genesisTeamId), $"Team {genesisTeamId:D}", CancellationToken.None);
        await activeTeam.SetActiveAsync(new TeamId(genesisTeamId), CancellationToken.None);

        await host.StartAsync();

        var worker = host.Services.GetRequiredService<LocalNodeWorker>();
        // Wait for the worker to bind B's own-team daemon (its boot posture). The bind happens on the
        // hosted-service thread (first resolve of the team's lazy ISyncDaemonTransport singleton), so a boot
        // bind collision does NOT throw out of host.StartAsync — it leaves BoundGossip null past the
        // deadline. Detect that explicitly and throw a typed, RETRYABLE signal (BootBOnAFreeFixedPortAsync
        // re-boots on a fresh port) AFTER disposing the half-booted host so it leaks nothing. listenForPeers
        // is always true here, so a registered-but-unbound daemon == a real bind failure (not a
        // legitimately sync-disabled team, which would never register an IGossipDaemon).
        var boundDeadline = Environment.TickCount64 + (long)TimeSpan.FromSeconds(30).TotalMilliseconds;
        while (Environment.TickCount64 < boundDeadline
            && (worker.BoundGossip is null || worker.BoundTeam is null))
        {
            await Task.Delay(50);
        }
        if (worker.BoundGossip is null || worker.BoundTeam is null)
        {
            try { await host.StopAsync(TimeSpan.FromSeconds(5)); } catch { /* best-effort */ }
            host.Dispose();
            throw new InvalidOperationException(
                $"B's worker did not bind its own-team gossip daemon at boot — the fixed bind {listenBindEndpoint} "
                + "is already held (a parallel test grabbed it in the GrabFreePort TOCTOU window).");
        }
        _cleanup.Add(new HostCleanup(host));

        var joinService = host.Services.GetRequiredService<NodeEnrollmentJoinService>();

        return new WorkerNode
        {
            Node = bNode,
            Worker = worker,
            Host = host,
            EndpointHolder = endpointHolder,
            JoinService = joinService,
        };
    }

    // ─────────────────────────────── recording SoD sink + single-team active accessor ───────────────────────

    /// <summary>A recording <see cref="IEnrollmentCompensatingControlRecorder"/> proving the PRODUCTION admit ran the SoD compensating-control
    /// (the second-set-of-eyes) — what the test-handler tests omitted (the re-review MINOR).</summary>
    private sealed class RecordingEnrollmentControlRecorder : IEnrollmentCompensatingControlRecorder
    {
        private readonly List<string> _admitted = new();
        public int MemberAdmittedCount { get; private set; }
        public IReadOnlyList<string> AdmittedParties => _admitted;

        public ValueTask RecordMemberAdmittedAsync(
            TenantId tenantId, string teamId, string admitterPartyId, string admittedPartyId,
            string admittedPublicKeyBase64Url, IReadOnlyList<string> grantedPermissions, string admissionMode,
            string? correlationId = null, CancellationToken ct = default)
        {
            MemberAdmittedCount++;
            _admitted.Add(admittedPartyId);
            return default;
        }

        public ValueTask RecordMemberRevokedAsync(
            TenantId tenantId, string teamId, string revokerPartyId, string revokedPartyId,
            string? correlationId = null, CancellationToken ct = default) => default;

        public ValueTask RecordPermissionsGrantedAsync(
            TenantId tenantId, string teamId, string granterPartyId, string targetPartyId,
            IReadOnlyList<string> resultingPermissions, string? correlationId = null,
            CancellationToken ct = default) => default;

        public ValueTask RecordOwnershipTransferredAsync(
            TenantId tenantId, string teamId, string fromPartyId, string toPartyId,
            string? correlationId = null, CancellationToken ct = default) => default;
    }

    /// <summary>A minimal single-team <see cref="IActiveTeamAccessor"/> for A: the production admitter resolves its
    /// team-scoped transport key from <c>Active.Services.GetService&lt;INodeIdentityProvider&gt;()</c> (the #1296-F2
    /// source of truth). Returns A's A-team identity.</summary>
    private sealed class SingleTeamActiveAccessor : IActiveTeamAccessor
    {
        private readonly TeamContext _ctx;
        public SingleTeamActiveAccessor(Guid teamId, NodeIdentity rootIdentity)
        {
            var teamIdentity = TeamScopedNodeIdentity.Derive(rootIdentity, teamId.ToString("D"), SubkeyDerivation);
            var sc = new ServiceCollection();
            sc.AddTestKernelClock();
            sc.AddSingleton<INodeIdentityProvider>(new InMemoryNodeIdentityProvider(teamIdentity));
            _ctx = new TeamContext(new TeamId(teamId), $"Team {teamId:D}", sc.BuildServiceProvider(), TimeProvider.System);
        }
        public TeamContext? Active => _ctx;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged { add { } remove { } }
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>
    /// No-op store activator — the gossip/enroll path under test never reads the encrypted store, so the join
    /// service's ActivateAsync is satisfied without opening a SQLCipher store (keeps the test native-free).
    /// <para>
    /// <b>MINOR-2 disposition (cerebrum [2026-06-21] verdict).</b> The verdict flagged that this no-op masks the
    /// real <c>ITeamStoreActivator.ActivateAsync</c> (the SQLCipher store-open on team-switch) in
    /// <c>NodeEnrollmentJoinService.JoinAsync</c> step 2. That real store-open is NOT uncovered:
    /// <list type="number">
    ///   <item>The activator seam itself — derive the SQLCipher key + call <c>IEncryptedStore.OpenAsync</c>,
    ///     plus its idempotency / per-team-key / not-materialized-throw semantics — is unit-covered by
    ///     <c>Harborline.Api.Kernel.Runtime.Tests.TeamStoreActivatorTests</c> against the REAL
    ///     <c>TeamStoreActivator</c>.</item>
    ///   <item>The DAEMON-REBIND path (the deep part this PR adds) is store-INDEPENDENT: the rebind reads only the
    ///     team's <c>IGossipDaemon</c> / <c>INodeIdentityProvider</c> / <c>ISyncDaemonTransport</c> — it never
    ///     resolves <c>IEncryptedStore</c>, so it does not assume the no-op. (Grep-verified: no
    ///     <c>IEncryptedStore</c> reference in <c>LocalNodeWorker</c> or <c>GossipDaemon</c>.)</item>
    ///   <item>The end-to-end real-SQLCipher store-open ON the joined team after switch is exercised by the
    ///     physical Mac↔Surface re-verify (HW-gated on the Surface being online), run on the FIXED-PORT config per
    ///     this PR's BLOCKER-1 fix.</item>
    /// </list>
    /// So the no-op here stands in ONLY for the native-SQLCipher dependency the gossip-path test deliberately
    /// avoids — it masks no logic the rebind relies on.
    /// </para>
    /// </summary>
    private sealed class NoopStoreActivator : ITeamStoreActivator
    {
        public ValueTask ActivateAsync(TeamId teamId, CancellationToken ct) => default;
    }

    // ─────────────────────────────── trust-gate + daemon + service helpers ───────────────────────

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
                RoundIntervalSeconds = 1, PeerPickCount = 1, ConnectTimeoutSeconds = 5, DeadPeerBackoffSeconds = 2,
            }),
            new InMemoryNodeIdentityProvider(identity),
            signer,
            deltaProducer: projection,
            deltaSink: projection,
            trustPolicy: trust, timeProvider: TimeProvider.System);

    private GossipDaemon BuildDaemonWithEnrollment(
        ISyncDaemonTransport transport,
        NodeIdentity identity,
        IEd25519Signer signer,
        ContactCrdtProjection projection,
        IPeerTrustPolicy trust,
        IPreTrustEnrollmentHandler enrollmentHandler) =>
        new(
            transport,
            new VectorClock(),
            Options.Create(new GossipDaemonOptions
            {
                RoundIntervalSeconds = 1, PeerPickCount = 1, ConnectTimeoutSeconds = 5, DeadPeerBackoffSeconds = 2,
            }),
            new InMemoryNodeIdentityProvider(identity),
            signer,
            deltaProducer: projection,
            deltaSink: projection,
            trustPolicy: trust,
            logger: null,
            enrollmentHandler: enrollmentHandler, timeProvider: TimeProvider.System);

    private static ServiceProvider BuildNodeServices(string dir, NodeTeamRoster roster)
    {
        var services = new ServiceCollection();
        services.AddTestKernelClock();
        services.AddLogging();
        services.AddSingleton<IHarborlineEntityModule, PeopleEntityModule>();
        services.AddDbContextFactory<LocalNodeDbContext>(opt =>
            opt.UseSqlite($"Data Source={Path.Combine(dir, "local-node.db")};Pooling=False"));
        services.AddSingleton<ICrdtEngine, YDotNetCrdtEngine>();
        return services.BuildServiceProvider();
    }

    private static ServiceProvider BuildRosterServices(string dir)
    {
        var services = new ServiceCollection();
        services.AddTestKernelClock();
        services.AddLogging();
        services.AddDbContextFactory<NodeLocalRosterDbContext>(opt =>
            opt.UseSqlite($"Data Source={Path.Combine(dir, "roster.db")};Pooling=False"));
        services.AddSingleton<ICrdtEngine, YDotNetCrdtEngine>();
        return services.BuildServiceProvider();
    }

    private string NewTempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"harborline-{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private static async Task<Party> CreateContactAsync(AdmitterNode r, string displayName) =>
        await CreateContactAsync(r.Factory, r.Projection, displayName);

    private static async Task<Party> CreateContactAsync(BNode r, string displayName) =>
        await CreateContactAsync(r.Factory, r.Projection, displayName);

    private static async Task<Party> CreateContactAsync(
        IDbContextFactory<LocalNodeDbContext> factory, ContactCrdtProjection projection, string displayName)
    {
        var party = Party.Create(LocalTenant, PartyKind.Person, displayName, Actor, new Instant(System.TimeProvider.System.GetUtcNow()));
        await using (var ctx = await factory.CreateDbContextAsync())
        {
            ctx.Set<Party>().Add(party);
            await ctx.SaveChangesAsync();
        }
        projection.ProjectUpsert(party);
        return party;
    }

    private static async Task<string?> ReadDisplayNameAsync(AdmitterNode r, PartyId id) =>
        await ReadDisplayNameAsync(r.Factory, id);

    private static async Task<string?> ReadDisplayNameAsync(BNode r, PartyId id) =>
        await ReadDisplayNameAsync(r.Factory, id);

    private static async Task<string?> ReadDisplayNameAsync(IDbContextFactory<LocalNodeDbContext> factory, PartyId id)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        var p = await ctx.Set<Party>().IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        return p?.DeletedAt is null ? p?.DisplayName : null;
    }

    private static async Task WaitForEfAsync(AdmitterNode r, PartyId id, string expected, string because)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(25);
        string? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = await ReadDisplayNameAsync(r, id);
            if (last == expected) return;
            await Task.Delay(150);
        }
        Assert.Fail($"{because} Last observed value: '{last ?? "(absent)"}', expected '{expected}'.");
    }

    private static async Task WaitForEfAsync(BNode r, PartyId id, string expected, string because)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(25);
        string? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = await ReadDisplayNameAsync(r, id);
            if (last == expected) return;
            await Task.Delay(150);
        }
        Assert.Fail($"{because} Last observed value: '{last ?? "(absent)"}', expected '{expected}'.");
    }

    /// <summary>
    /// Poll <paramref name="condition"/> until it holds or the deadline elapses. The deadline is generous
    /// (CI runners are slower + more contended than dev machines), and the poll is monotonic-clock-based
    /// (<see cref="Environment.TickCount64"/>, NOT wall-clock <c>DateTime.UtcNow</c>, which can jump under
    /// NTP / VM time-skew and silently shorten or lengthen the wait).
    /// <para>
    /// <paramref name="faultProbe"/> lets a REBIND wait fail FAST + LOUD: the worker's daemon-rebind runs
    /// off the join's call stack, so a faulted rebind (e.g. a transient port-bind collision) was previously
    /// invisible here — the condition simply never went true and the wait timed out with an opaque message
    /// that got re-run-lottery'd. When supplied, a non-null fault string short-circuits the wait with the
    /// REAL reason (the <c>RebindOutcome.FaultSummary</c>), so a genuine rebind fault surfaces as itself
    /// rather than a generic timeout.
    /// </para>
    /// </summary>
    private static async Task WaitUntilAsync(Func<bool> condition, string because, Func<string?>? faultProbe = null)
    {
        var deadline = Environment.TickCount64 + (long)TimeSpan.FromSeconds(30).TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return;
            var fault = faultProbe?.Invoke();
            if (fault is not null)
            {
                Assert.Fail($"{because} The daemon-rebind FAULTED before the condition was met: {fault}");
            }
            await Task.Delay(50);
        }
        // Final fault check so a rebind that faulted on the very last poll reports the real reason.
        var lastFault = faultProbe?.Invoke();
        Assert.True(condition(),
            lastFault is null ? because : $"{because} Last rebind fault: {lastFault}");
    }

    private sealed class ProjectionCleanup : IAsyncDisposable
    {
        private readonly ContactCrdtProjection _projection;
        private readonly ServiceProvider _sp;
        public ProjectionCleanup(ContactCrdtProjection projection, ServiceProvider sp) { _projection = projection; _sp = sp; }
        public async ValueTask DisposeAsync() { await _projection.DisposeAsync(); await _sp.DisposeAsync(); }
    }

    private sealed class RosterProjectionCleanup : IAsyncDisposable
    {
        private readonly RosterCrdtProjection _projection;
        private readonly ServiceProvider _sp;
        public RosterProjectionCleanup(RosterCrdtProjection projection, ServiceProvider sp) { _projection = projection; _sp = sp; }
        public async ValueTask DisposeAsync() { await _projection.DisposeAsync(); await _sp.DisposeAsync(); }
    }

    private sealed class HostCleanup : IAsyncDisposable
    {
        private readonly IHost _host;
        public HostCleanup(IHost host) => _host = host;
        public async ValueTask DisposeAsync()
        {
            try { await _host.StopAsync(TimeSpan.FromSeconds(5)); } catch { /* best-effort */ }
            _host.Dispose();
        }
    }
}
