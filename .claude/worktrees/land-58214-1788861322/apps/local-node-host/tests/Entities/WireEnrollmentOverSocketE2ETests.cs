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
using Harborline.Api.LocalNodeHost.Enrollment;
using Harborline.Api.LocalNodeHost.Health;

using Ed25519Signer = Harborline.Api.Kernel.Security.Crypto.Ed25519Signer;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// THE TEST THAT CATCHES WHAT THE IN-PROC TEST MASKED (#1301 F-1). The two-sided wire enrollment trust bootstrap
/// END-TO-END OVER A REAL SOCKET — two independent-genesis nodes, ZERO hand-provisioning, and the enrollment
/// exchange crossing an actual TCP socket boundary (NOT <c>InProcessAdmitterTransport</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>The gap this test closes (council F-1).</b> <see cref="TwoSidedWireEnrollmentE2ETests"/> proved the
/// enrollment PROTOCOL is sound, but its transport was <c>InProcessAdmitterTransport</c> — it called A's admit
/// logic IN-PROCESS, never crossing a socket. So it could not catch that the production transport (loopback HTTP
/// <c>/admission/redeem</c>) is unreachable from a remote machine. THIS test wires the REAL cross-machine path:
/// <list type="number">
///   <item>A runs a REAL <see cref="TcpSyncDaemonTransport"/> network LISTENER (<c>tcp://127.0.0.1:0</c>) with a
///     <see cref="IPreTrustEnrollmentHandler"/> wired into its <see cref="GossipDaemon"/> accept loop — the
///     #1301 F-1 pre-trust enrollment phase on the 7473-style sync listener.</item>
///   <item>B enrolls via the REAL <see cref="SocketEnrollmentTransport"/> — it DIALS A's listener over a TCP
///     socket, sends an <c>ENROLL_REQUEST</c> frame, reads the <c>ENROLL_RESPONSE</c>. The ONLY trust input is
///     the invite + its out-of-band anchor.</item>
///   <item>After enrollment: mutual trust (both policies hold both keys), the roster converges, comms cross BOTH
///     ways over the REAL gossip wire, and a NO-INVITE / BAD-INVITE remote peer is REJECTED with no info leak.</item>
/// </list>
/// A loopback TCP socket is a genuine socket boundary (separate accept/connect, real framing, real
/// serialize/deserialize) — the same code path a Mac↔Surface dial exercises, minus the physical NIC. The
/// enrollment exchange demonstrably crosses it: if the F-1 transport were still in-proc this test could not be
/// written (there would be no network listener offering the enrollment phase).
/// </para>
/// </remarks>
public sealed class WireEnrollmentOverSocketE2ETests : IAsyncLifetime
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
    // THE headline test — enrollment OVER A REAL SOCKET, then mutual trust + sync + reject.
    // ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "two-sided wire enrollment OVER A REAL SOCKET: B dials A's network listener (TCP), enrolls from the invite alone → mutual trust, roster converges, comms cross both ways, no-invite remote peer rejected with no leak")]
    public async Task EnrollmentOverRealSocket_MutualTrust_RosterConverges_CommsCrossBothWays_NoInvitePeerRejected()
    {
        var signer = new Ed25519Signer();

        // ── A: an INDEPENDENT node. Its own root, genesis team, roster, admission seam. ────────────────────────
        var a = NewNode("A", teamSeed: "team-A-office");
        // ── B: an INDEPENDENT node. DISTINCT root, DISTINCT own-team, DISTINCT genesis. No knowledge of A. ─────
        var b = NewNode("B", teamSeed: "team-B-home");

        Assert.NotEqual(a.Roster.Current.TeamId, b.Roster.Current.TeamId);
        Assert.NotEqual(a.Roster.Current.GenesisPartyId, b.Roster.Current.GenesisPartyId);
        Assert.Empty(a.Roster.TrustedTransportKeys());
        Assert.Empty(b.Roster.TrustedTransportKeys());

        // ── A mints a REAL invite (token + out-of-band anchor B pins). ─────────────────────────────────────────
        var anchor = TeamTrustAnchor.FromRoster(a.Roster.Current);
        var invite = a.Coordinator.CreateInvite(anchor);

        // ── A stands up a REAL NETWORK ENROLLMENT LISTENER (the #1301 F-1 pre-trust phase on the sync listener). A
        // GossipDaemon over a TCP transport, with the pre-trust enrollment handler wired. B will DIAL this. ──────
        var aDeviceForEnroll = a.TeamScopedIdentity(a.Roster.Current.TeamId);   // A's A-team HELLO identity.
        var aEnrollTransport = new TcpSyncDaemonTransport("tcp://127.0.0.1:0");
        _cleanup.Add(aEnrollTransport);
        var aEnrollEndpoint = aEnrollTransport.ListenEndpoint!;                  // the socket B dials.
        var aEnrollDaemon = BuildDaemonWithEnrollment(
            aEnrollTransport, aDeviceForEnroll, signer, a.Projection,
            TrustPolicyFor(aDeviceForEnroll.PublicKey, a.Roster),
            new TestPreTrustEnrollmentHandler(a));
        _cleanup.Add(aEnrollDaemon);
        await aEnrollDaemon.StartListeningAsync(CancellationToken.None);

        // ── B ENROLLS OVER THE REAL SOCKET. SocketEnrollmentTransport DIALS A's listener, sends ENROLL_REQUEST,
        // reads ENROLL_RESPONSE. NodeWireEnrollmentClient validates against the anchor + adopts. NOT in-proc. ────
        var socketTransport = new SocketEnrollmentTransport(
            admitterEndpoint: () => aEnrollEndpoint,
            timeout: TimeSpan.FromSeconds(20),
            logger: NullLogger<SocketEnrollmentTransport>.Instance);
        var outcome = await b.EnrollmentClient.EnrollAsync(
            invite.TokenId, anchor, socketTransport, CancellationToken.None);

        Assert.True(outcome.Succeeded,
            $"B must enroll OVER THE SOCKET from the invite alone. Reason: {outcome.FailureReason}");
        Assert.Equal(a.Roster.Current.TeamId, outcome.TeamId);

        // Stop the enrollment listener now that B has adopted (the trusted HELLO uses fresh daemons below).
        await aEnrollDaemon.StopListeningAsync(CancellationToken.None);

        // ── MUTUAL TRUST: both policies hold BOTH transport keys for A.teamId (computed from the post-enrollment
        // rosters, NOT hand-seeded). ─────────────────────────────────────────────────────────────────────────
        var aTransportKey = a.TransportKeyForTeam(a.Roster.Current.TeamId);
        var bTransportKeyForATeam = b.EnrollmentClient.DeriveTransportPublicKeyForTeam(
            a.Roster.Current.TeamId.ToString("D"));
        Assert.True(outcome.TransportPublicKey!.AsSpan().SequenceEqual(bTransportKeyForATeam),
            "the key B reports presenting must be its A-team-scoped subkey (the #1296-F2 JOINED-team scope).");

        var aTrustKeys = UnionFloorAndPeers(aTransportKey, a.Roster);
        Assert.Contains(aTrustKeys, k => k.AsSpan().SequenceEqual(aTransportKey));            // A trusts itself.
        Assert.Contains(aTrustKeys, k => k.AsSpan().SequenceEqual(bTransportKeyForATeam));    // A trusts B.

        var bTrustKeys = UnionFloorAndPeers(bTransportKeyForATeam, b.Roster);
        Assert.Contains(bTrustKeys, k => k.AsSpan().SequenceEqual(bTransportKeyForATeam));    // B trusts itself.
        Assert.Contains(bTrustKeys, k => k.AsSpan().SequenceEqual(aTransportKey));            // B trusts A.

        // ── ROSTER CONVERGED on BOTH nodes, validates to A's genesis. ─────────────────────────────────────────
        Assert.True(a.Roster.Current.Contains(b.PartyId), "A's roster must contain B (A admitted B over the socket).");
        Assert.True(a.Roster.Current.ValidatesToGenesis(Verifier));
        Assert.True(b.Roster.Current.Contains(b.PartyId), "B's adopted roster must contain B.");
        Assert.True(b.Roster.Current.Contains(a.PartyId), "B's adopted roster must contain A (the admitter).");
        Assert.True(b.Roster.Current.ValidatesToGenesis(Verifier), "B's adopted roster must validate to A's genesis.");
        Assert.Equal(a.PartyId, b.Roster.Current.GenesisPartyId);

        // ── COMMS CROSS BOTH WAYS over the REAL gossip wire (the trusted HELLO this enrollment bootstrapped). ──
        var deviceA = a.TeamScopedIdentity(a.Roster.Current.TeamId);
        var deviceB = b.TeamScopedIdentity(a.Roster.Current.TeamId);
        Assert.NotEqual(deviceA.PublicKey, deviceB.PublicKey);

        // B listens; A dials B.
        var transportBListen = new TcpSyncDaemonTransport("tcp://127.0.0.1:0");
        _cleanup.Add(transportBListen);
        var endpointB = transportBListen.ListenEndpoint!;
        var daemonB = BuildDaemon(transportBListen, deviceB, signer, b.Projection, TrustPolicyFor(deviceB.PublicKey, b.Roster));
        _cleanup.Add(daemonB);
        await daemonB.StartListeningAsync(CancellationToken.None);

        var transportAOut = new TcpSyncDaemonTransport();
        _cleanup.Add(transportAOut);
        var daemonA = BuildDaemon(transportAOut, deviceA, signer, a.Projection, TrustPolicyFor(deviceA.PublicKey, a.Roster));
        _cleanup.Add(daemonA);
        daemonA.AddPeer(endpointB, deviceB.PublicKey);
        await daemonA.StartAsync(CancellationToken.None);

        var fromA = await CreateContactAsync(a, "Ada Lovelace");
        await WaitForEfAsync(b, fromA.Id, expected: "Ada Lovelace",
            because: "after SOCKET enrollment, A→B comms must cross the trusted session (the bootstrap that "
                + "never worked cross-machine before).");

        // B→A also crosses (mutual): A listens, B dials A.
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
            because: "after SOCKET enrollment, B→A comms must also cross (mutual trust).");

        await daemonA.StopAsync(CancellationToken.None);
        await daemonBOut.StopAsync(CancellationToken.None);
        await daemonB.StopListeningAsync(CancellationToken.None);
        await daemonAListen.StopListeningAsync(CancellationToken.None);

        // ── A NO-INVITE remote peer that DIALS the enrollment listener is REJECTED with NO info leak. ──────────
        // Re-arm A's enrollment listener and have a stranger send a BAD-INVITE enroll request over the socket. The
        // handler must reject fail-closed (Accepted=false) and leak NO roster/genesis — the joiner adopts nothing.
        var aEnrollTransport2 = new TcpSyncDaemonTransport("tcp://127.0.0.1:0");
        _cleanup.Add(aEnrollTransport2);
        var aEnrollEndpoint2 = aEnrollTransport2.ListenEndpoint!;
        var aEnrollDaemon2 = BuildDaemonWithEnrollment(
            aEnrollTransport2, aDeviceForEnroll, signer, a.Projection,
            TrustPolicyFor(aDeviceForEnroll.PublicKey, a.Roster),
            new TestPreTrustEnrollmentHandler(a));
        _cleanup.Add(aEnrollDaemon2);
        await aEnrollDaemon2.StartListeningAsync(CancellationToken.None);

        // Charlie: a stranger with NO valid invite. It enrolls with a BOGUS token id over the real socket.
        var charlie = NewNode("Charlie", teamSeed: "team-A-office");
        var charlieAnchorPin = anchor; // even pinning A's real anchor, a bad token cannot redeem.
        var charlieSocket = new SocketEnrollmentTransport(
            admitterEndpoint: () => aEnrollEndpoint2,
            timeout: TimeSpan.FromSeconds(20),
            logger: NullLogger<SocketEnrollmentTransport>.Instance);
        var charlieOutcome = await charlie.EnrollmentClient.EnrollAsync(
            tokenId: Guid.NewGuid().ToString("N"),       // a token A never minted → invite_rejected.
            inviteAnchor: charlieAnchorPin,
            transport: charlieSocket,
            CancellationToken.None);

        Assert.False(charlieOutcome.Succeeded,
            "a no-invite remote peer must be REJECTED over the socket — the invite is trust, not reachability.");
        // No leak: Charlie adopted nothing; its roster is still its OWN genesis team, trust set empty toward A.
        Assert.Empty(charlie.Roster.TrustedTransportKeys());
        Assert.NotEqual(a.Roster.Current.TeamId, charlie.Roster.Current.TeamId);
        // A never admitted Charlie (the bad token never redeemed).
        Assert.False(a.Roster.Current.Contains(charlie.PartyId), "A must NOT have admitted the no-invite peer.");

        await aEnrollDaemon2.StopListeningAsync(CancellationToken.None);
    }

    // ───────────────────────────── node fixture (a self-contained independent node) ─────────────────────────

    private sealed class NodeFixture
    {
        public required string Name { get; init; }
        public required NodeIdentity RootIdentity { get; init; }
        public required NodePrincipalSigner Signer { get; init; }
        public required string PartyId { get; init; }
        public required NodeTeamRoster Roster { get; init; }
        public required AdmissionCoordinator Coordinator { get; init; }
        public required NodeWireEnrollmentClient EnrollmentClient { get; init; }
        public required IDbContextFactory<LocalNodeDbContext> Factory { get; init; }
        public required ContactCrdtProjection Projection { get; init; }

        public byte[] TransportKeyForTeam(Guid teamId) =>
            TeamScopedNodeIdentity.Derive(RootIdentity, teamId.ToString("D"), SubkeyDerivation).PublicKey;

        public NodeIdentity TeamScopedIdentity(Guid teamId) =>
            TeamScopedNodeIdentity.Derive(RootIdentity, teamId.ToString("D"), SubkeyDerivation);
    }

    private NodeFixture NewNode(string name, string teamSeed)
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

        var dir = Path.Combine(Path.GetTempPath(), $"harborline-socket-enroll-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHarborlineEntityModule, PeopleEntityModule>();
        services.AddDbContextFactory<LocalNodeDbContext>(opt =>
            opt.UseSqlite($"Data Source={Path.Combine(dir, "local-node.db")};Pooling=False"));
        services.AddSingleton<ICrdtEngine, YDotNetCrdtEngine>();
        var sp = services.BuildServiceProvider();
        var factory = sp.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
        using (var ctx = factory.CreateDbContext()) ctx.Database.EnsureCreated();
        var projection = new ContactCrdtProjection(
            sp.GetRequiredService<ICrdtEngine>(), factory, NullLogger<ContactCrdtProjection>.Instance);
        _cleanup.Add(new ProjectionCleanup(projection, sp));

        return new NodeFixture
        {
            Name = name,
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
    /// The A-side <see cref="IPreTrustEnrollmentHandler"/> for the socket test — runs the EXACT reviewed-sound
    /// admit protocol (verify B's signed request → redeem the invite single-use + admit → wire B's transport key
    /// into A's trust map → build the bootstrap response) over the OPAQUE byte payload the daemon delivers. This is
    /// the same admit logic the production <c>WireEnrollmentAdmitter</c> + <c>NodeEnrollmentAdmitter</c> run; the
    /// test omits ONLY the host-runtime side-effects (roster-sync publish + SoD audit), which are not what the F-1
    /// transport gap is about (those are covered by the route + the in-proc test). The point here is that the
    /// exchange crosses a REAL SOCKET via the daemon's accept loop, not in-process.
    /// </summary>
    private sealed class TestPreTrustEnrollmentHandler : IPreTrustEnrollmentHandler
    {
        private readonly NodeFixture _admitter;
        public TestPreTrustEnrollmentHandler(NodeFixture admitter) => _admitter = admitter;

        public Task<PreTrustEnrollmentResult> HandleAsync(
            byte[] requestPayload,
            string source,
            CancellationToken ct)
        {
            EnrollmentRequest request;
            try
            {
                request = EnrollmentWireCodec.DecodeRequest(requestPayload ?? Array.Empty<byte>());
            }
            catch
            {
                return Task.FromResult(PreTrustEnrollmentResult.Reject("malformed_request"));
            }

            // (1) verify the joiner's signature (proof-of-possession).
            if (!WireEnrollment.VerifyRequest(request, Verifier))
                return Task.FromResult(PreTrustEnrollmentResult.Reject("enroll_signature_invalid"));

            // (2) redeem single-use + admit. A bad token / replay / expiry → fail-closed reject, no leak.
            var joiningPrincipal = PrincipalId.FromBase64Url(request.JoiningPrincipalPublicKey);
            var admit = _admitter.Coordinator.AdmitOverInvite(
                _admitter.Roster.Current, request.TokenId, _admitter.PartyId, _admitter.Signer.Signer,
                request.JoiningPartyId, joiningPrincipal, PermissionCompositions.Member);
            if (!admit.Admitted_ || admit.Roster is null)
                return Task.FromResult(PreTrustEnrollmentResult.Reject("invite_rejected"));

            // (3) wire B's transport key into A's trust map (A now trusts B's wire HELLO).
            var joiningTransport = PrincipalId.FromBase64Url(request.JoiningTransportPublicKey).AsSpan().ToArray();
            _admitter.Roster.AdmitPeer(admit.Roster, request.JoiningPartyId, joiningTransport);

            // (4) build the A→B bootstrap response + encode to the opaque payload the daemon writes back.
            var admitterTransportKey = _admitter.TransportKeyForTeam(admit.Roster.TeamId);
            var memberKeys = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                [_admitter.PartyId] = admitterTransportKey,
            };
            foreach (var (party, key) in _admitter.Roster.AdmittedPeerTransportKeys())
                memberKeys[party] = key;

            var response = WireEnrollment.BuildResponse(admit.Roster, admitterTransportKey, memberKeys);
            return Task.FromResult(PreTrustEnrollmentResult.Accept(EnrollmentWireCodec.EncodeResponse(response)));
        }
    }

    // ───────────────────────────── trust-gate + daemon helpers ─────────────────────────

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
                RoundIntervalSeconds = 1,
                PeerPickCount = 1,
                ConnectTimeoutSeconds = 5,
                DeadPeerBackoffSeconds = 2,
            }),
            new InMemoryNodeIdentityProvider(identity),
            signer,
            deltaProducer: projection,
            deltaSink: projection,
            trustPolicy: trust,
            logger: null,
            enrollmentHandler: enrollmentHandler, timeProvider: TimeProvider.System);

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
