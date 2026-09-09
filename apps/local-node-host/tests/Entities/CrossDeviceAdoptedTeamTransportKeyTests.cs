using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;

using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Crdt;
using Harborline.Api.Kernel.Crdt.Backends;
using Harborline.Api.Kernel.Security.Crypto;
using Harborline.Api.Kernel.Security.Keys;
using Harborline.Api.Kernel.Sync.Gossip;
using Harborline.Api.Kernel.Sync.Handshake;
using Harborline.Api.Kernel.Sync.Identity;
using Harborline.Api.Kernel.Sync.Protocol;
using Harborline.Api.LocalNodeHost.Data.Roster;
using Harborline.Api.LocalNodeHost.Enrollment;

using Ed25519Signer = Harborline.Api.Kernel.Security.Crypto.Ed25519Signer;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// THE CROSS-DEVICE TRANSPORT-KEY REPRO (bug-TRANSPORT-KEY, the sibling of bug-1332). Reproduces the
/// <c>PEER_UNTRUSTED</c> the cross-device verify hit (Surface + Mac both joining A → A's <c>/sync-status</c> shows
/// each joiner <c>state=couldnt errorCode=PEER_UNTRUSTED isSecurityEvent=true</c>), in a FAITHFUL model of the
/// JOINER lifecycle the whole prior suite MASKED.
/// </summary>
/// <remarks>
/// <para>
/// <b>What every prior test masked.</b> <see cref="ThreeNodeMeshTransportKeyTests"/> +
/// <see cref="BSideEnrollmentJoinE2ETests"/> + <see cref="TwoSidedWireEnrollmentE2ETests"/> all either (a) seeded
/// the joiner directly FROM A's genesis (so the joiner never had its own genesis on its synced doctype, and never
/// superseded it), or (b) checked trust DIRECTLY off the one-time <see cref="NodeTeamRoster.AdoptEnrollment"/> set
/// — NEVER after a roster-doctype reconcile re-ran <see cref="NodeTeamRoster.AdoptSyncedRoster"/> on a node that
/// joined via its OWN DISTINCT genesis team. That is precisely the path the live cross-device daemon hammers (it
/// runs continuous anti-entropy rounds → reconcile → rebuild-from-converged-roster), and it is the SAME gap
/// bug-1332 had: the one-time adopt was correct, but the steady-state rebuild was scoped wrong.
/// </para>
/// <para>
/// <b>The faithful joiner lifecycle this models (the cross-device sequence):</b>
/// <list type="number">
///   <item>B BOOTS on its OWN distinct genesis team (B-team) and seeds its OWN genesis self-admission onto its
///     synced roster doctype — CARRYING B's B-team-scoped transport key (what the boot RosterSyncBootstrapHostedService
///     does).</item>
///   <item>A founds A-team, mints an invite, and ADMITS B over the wire — recording + publishing B's
///     <em>A-team-scoped</em> transport key <c>HKDF(B-root, A.teamId)</c> (the #1296-F2 / #1310 path).</item>
///   <item>B ADOPTS A's team (<see cref="NodeTeamRoster.AdoptEnrollment"/>) and SUPERSEDES its own B-team genesis
///     records from its doctype (<see cref="RosterCrdtProjection.SupersedeOwnTeamRecordsAsync"/>).</item>
///   <item>The roster doctype CONVERGES between A and B (the steady-state daemon rounds) → each node's
///     <see cref="RosterCrdtProjection.RebuildLiveRoster"/> re-runs <see cref="NodeTeamRoster.AdoptSyncedRoster"/>
///     from the converged records.</item>
/// </list>
/// </para>
/// <para>
/// <b>The assertion (refute-verified).</b> AFTER the converged-roster rebuild, A must still TRUST B's A-team HELLO
/// key and B must still trust A's — i.e. the transport-trust set must FOLLOW the ADOPTED team through the rebuild,
/// not regress to <c>PEER_UNTRUSTED</c>. The companion REFUTE test confirms a non-enrolled stranger STAYS
/// <c>PEER_UNTRUSTED</c> (the fix must not flip a real distrust into a wrongful trust).
/// </para>
/// </remarks>
public sealed class CrossDeviceAdoptedTeamTransportKeyTests : IAsyncLifetime
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

    private static readonly IOperationVerifier Verifier = new Ed25519Verifier();
    private static readonly IEd25519Signer TransportSigner = new Ed25519Signer();
    private static readonly ITeamSubkeyDerivation SubkeyDerivation = new TeamSubkeyDerivation(TransportSigner);

    // ── A node: a comms/principal identity (the roster binding) + a distinct node ROOT (the source of its
    //    team-scoped transport subkey — material no other node holds), like two distinct installs. Each node also
    //    owns its OWN genesis team (distinct per node — the real cross-device case). ───────────────────────────
    private sealed record Node(
        string PartyId, KeyPair PrincipalKey, IOperationSigner PrincipalSigner, NodeIdentity Root, Guid OwnTeam)
    {
        public static Node New(string partyId, string ownTeamSeed)
        {
            var kp = KeyPair.Generate();
            var rootSeed = new byte[32];
            System.Security.Cryptography.RandomNumberGenerator.Fill(rootSeed);
            var (rootPub, rootPriv) = TransportSigner.GenerateFromSeed(rootSeed);
            var nodeId = Convert.ToHexString(rootPub.AsSpan(0, 16)).ToLowerInvariant();
            var root = new NodeIdentity(nodeId, rootPub, rootPriv);
            // A DISTINCT genesis team per node — deterministic from (seed, nodeId), like two distinct installs.
            var ownTeam = new Guid(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(ownTeamSeed + ":" + nodeId)).AsSpan(0, 16).ToArray());
            return new Node(partyId, kp, new Harborline.Api.Foundation.Crypto.Ed25519Signer(kp), root, ownTeam);
        }

        /// <summary>This node's team-scoped TRANSPORT identity for a given team (HKDF(root, teamId)) — the wire
        /// HELLO identity it presents once that team is its active team.</summary>
        public NodeIdentity TransportIdentity(Guid teamId) =>
            TeamScopedNodeIdentity.Derive(Root, teamId.ToString("D"), SubkeyDerivation);

        public byte[] TransportPublicKey(Guid teamId) => TransportIdentity(teamId).PublicKey;
    }

    // ── A roster-sync replica: own CRDT engine, own persisted store, own live roster + projection. ────────────
    private sealed class Replica : IAsyncDisposable
    {
        public required string Name { get; init; }
        public required string Dir { get; init; }
        public required ServiceProvider Sp { get; init; }
        public required IDbContextFactory<NodeLocalRosterDbContext> Factory { get; init; }
        public required RosterCrdtProjection Projection { get; init; }
        public required NodeTeamRoster NodeRoster { get; init; }

        public async ValueTask DisposeAsync()
        {
            await Projection.DisposeAsync();
            await Sp.DisposeAsync();
            try { if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    private async Task<Replica> NewReplicaAsync(string name, MemberRoster seedRoster, IOperationSigner attestationSigner)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"harborline-xdev-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        var connectionString = $"Data Source={Path.Combine(dir, "roster.db")};Pooling=False";

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContextFactory<NodeLocalRosterDbContext>(opt => opt.UseSqlite(connectionString));
        services.AddSingleton<ICrdtEngine, YDotNetCrdtEngine>();

        var sp = services.BuildServiceProvider();
        var factory = sp.GetRequiredService<IDbContextFactory<NodeLocalRosterDbContext>>();
        await using (var ctx = await factory.CreateDbContextAsync())
            await ctx.Database.EnsureCreatedAsync();

        var nodeRoster = new NodeTeamRoster(seedRoster);
        var projection = new RosterCrdtProjection(TimeProvider.System,
            sp.GetRequiredService<ICrdtEngine>(), factory, Verifier, attestationSigner,
            NullLogger<RosterCrdtProjection>.Instance, nodeRoster);

        var replica = new Replica
        {
            Name = name, Dir = dir, Sp = sp, Factory = factory, Projection = projection, NodeRoster = nodeRoster,
        };
        _cleanup.Add(replica);
        return replica;
    }

    // ── helpers mirroring the production seed / admit / converge path ──────────────────────────────────────────

    private static MemberRoster GenesisFor(Node founder, Guid team) =>
        MemberRoster.StableGenesis(team, founder.PartyId, founder.PrincipalSigner, Verifier);

    /// <summary>Seed a node's OWN genesis self-admission onto its synced doctype CARRYING its OWN team-scoped
    /// transport key — exactly what RosterSyncBootstrapHostedService does at boot, for whatever team is genesis.</summary>
    private static async Task SeedOwnGenesisAsync(Replica r, Node node, Guid team) =>
        await r.Projection.PublishLocalAsync(
            RosterRecordCrdtState.FromAdmission(
                r.NodeRoster.Current.EnumerateAdmissions().Single(), node.TransportPublicKey(team)),
            CancellationToken.None);

    /// <summary>The A-side admit: A admits the joiner into A-team, records the joiner's A-team transport key
    /// locally (AdmitPeer), and publishes the admission CARRYING the joiner's A-team transport key — exactly what
    /// <see cref="WireEnrollmentAdmitter"/> does over the wire.</summary>
    private static async Task AdmitAndPublishAsync(Replica a, Node founder, Guid aTeam, Node joiner)
    {
        var withJoiner = a.NodeRoster.Current.Admit(
            founder.PartyId, founder.PrincipalSigner, joiner.PartyId, joiner.PrincipalKey.PrincipalId,
            PermissionCompositions.Member, Verifier, DateTimeOffset.UtcNow, Guid.NewGuid());
        var joinerATeamKey = joiner.TransportPublicKey(aTeam);
        a.NodeRoster.AdmitPeer(withJoiner, joiner.PartyId, joinerATeamKey);
        var rec = withJoiner.EnumerateAdmissions().Single(x => x.PartyId == joiner.PartyId);
        await a.Projection.PublishLocalAsync(
            RosterRecordCrdtState.FromAdmission(rec, joinerATeamKey), CancellationToken.None);
    }

    private static async Task SyncDirectAsync(Replica src, Replica dst)
    {
        var delta = await src.Projection.EncodeOutboundDeltaAsync(
            RosterCrdtProjection.DocumentId, dst.Projection.VectorClock, CancellationToken.None);
        Assert.NotNull(delta);
        await dst.Projection.ApplyInboundDeltaAsync(
            RosterCrdtProjection.DocumentId, 1, delta!.Value, CancellationToken.None);
        await dst.Projection.DrainPendingReconcilesAsync();
    }

    private static async Task ConvergeAllAsync(params Replica[] replicas)
    {
        for (var round = 0; round < 3; round++)
        {
            foreach (var src in replicas)
                foreach (var dst in replicas)
                    if (!ReferenceEquals(src, dst))
                        await SyncDirectAsync(src, dst);
        }
        foreach (var r in replicas)
        {
            await r.Projection.ReconcileAsync(CancellationToken.None);
            await r.Projection.DrainPendingReconcilesAsync();
        }
    }

    /// <summary>The trust gate the per-team registrar builds: {own A-team subkey floor} ∪ {the live roster's
    /// transport keys}. Reads the snapshot on each call (convergence applies live).</summary>
    private static MemberSetTrustPolicy TrustPolicyFor(NodeTeamRoster roster, byte[] ownFloorKey) =>
        new(() =>
        {
            var keys = new List<byte[]> { ownFloorKey };
            keys.AddRange(roster.TrustedTransportKeys());
            return keys;
        });

    private static HelloMessage HelloFor(NodeIdentity transportIdentity) => new(
        NodeId: transportIdentity.NodeIdBytes,
        SchemaVersion: "1",
        SupportedVersions: new[] { "1" },
        PublicKey: transportIdentity.PublicKey,
        Timestamp: 0UL,
        Signature: Array.Empty<byte>());

    private static GossipDaemon BuildDaemon(
        ISyncDaemonTransport transport,
        NodeIdentity identity,
        IEd25519Signer signer,
        RosterCrdtProjection projection,
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

    private static async Task WaitForRosterMemberAsync(Replica r, string partyId, string because)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (r.NodeRoster.Current.Contains(partyId)) return;
            await Task.Delay(150);
        }
        Assert.Fail($"{because} Party '{partyId}' never converged into {r.Name}'s roster.");
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════
    // THE HEADLINE REPRO: a joiner with its OWN distinct genesis joins A's team; after the roster doctype
    // converges (the steady-state rebuild path), A↔B MUTUAL transport trust must HOLD — not regress to
    // PEER_UNTRUSTED. RED before the fix (the cross-device wall), GREEN after.
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════

    [Fact(DisplayName = "cross-device adopt: a joiner with its OWN distinct genesis joins A's team; after the roster doctype CONVERGES (the steady-state AdoptSyncedRoster rebuild), A still TRUSTS B's A-team HELLO and B still trusts A (transport trust FOLLOWS the adopted team — NOT PEER_UNTRUSTED). The bug-1332 transport-key sibling.")]
    public async Task Joiner_OwnGenesis_JoinsATeam_TransportTrust_Survives_SyncedRosterRebuild_BothWays()
    {
        var a = Node.New("os:A#founder", ownTeamSeed: "team-A-office");
        var b = Node.New("os:B#bob", ownTeamSeed: "team-B-home");
        var aTeam = a.OwnTeam; // A's genesis team is the team B will adopt.

        // ── A: founds A-team, seeds its own genesis CARRYING A's A-team transport key (boot). ──────────────────
        var aReplica = await NewReplicaAsync("A", GenesisFor(a, aTeam), a.PrincipalSigner);
        await SeedOwnGenesisAsync(aReplica, a, aTeam);

        // ── B: BOOTS on its OWN DISTINCT genesis team (B-team), seeds B's own genesis CARRYING B's B-team key. ──
        // This is the crux the prior suite skipped: B has its OWN genesis on its OWN doctype BEFORE it joins.
        var bReplica = await NewReplicaAsync("B", GenesisFor(b, b.OwnTeam), b.PrincipalSigner);
        await SeedOwnGenesisAsync(bReplica, b, b.OwnTeam);
        Assert.NotEqual(aTeam, b.OwnTeam);

        // ── A ADMITS B over the wire — records + publishes B's A-team-scoped transport key (#1296-F2 / #1310). ──
        await AdmitAndPublishAsync(aReplica, a, aTeam, b);

        // ── B ADOPTS A's team: replace B's roster with A's validated roster + the enrolled-member transport set
        //    (A's A-team key + B's A-team key), exactly as NodeWireEnrollmentClient.AdoptEnrollment does. ────────
        var aPostAdmitRoster = aReplica.NodeRoster.Current; // A's roster after admitting B (validates to A's genesis).
        var enrolledTransportKeys = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [a.PartyId] = a.TransportPublicKey(aTeam),
            [b.PartyId] = b.TransportPublicKey(aTeam),
        };
        bReplica.NodeRoster.AdoptEnrollment(aPostAdmitRoster, enrolledTransportKeys);

        // ── B SUPERSEDES its own (now-superseded) B-team genesis records from its synced doctype (BLOCKER-2). ──
        await bReplica.Projection.SupersedeOwnTeamRecordsAsync(b.OwnTeam, CancellationToken.None);
        await bReplica.Projection.DrainPendingReconcilesAsync();

        // Pre-converge sanity: the one-time AdoptEnrollment set already trusts both ways (this is what the prior
        // suite checked and stopped at — it PASSES here too; the bug is downstream, in the rebuild).
        var bATeamKey = b.TransportPublicKey(aTeam);
        var aATeamKey = a.TransportPublicKey(aTeam);
        Assert.Contains(bReplica.NodeRoster.TrustedTransportKeys(), k => k.AsSpan().SequenceEqual(aATeamKey));
        Assert.Contains(aReplica.NodeRoster.TrustedTransportKeys(), k => k.AsSpan().SequenceEqual(bATeamKey));

        // ── THE STEADY STATE: the roster doctype converges A↔B → each node's RebuildLiveRoster re-runs
        //    AdoptSyncedRoster from the converged records. THIS is what the live daemon does every round, and what
        //    the cross-device verify hit. ──────────────────────────────────────────────────────────────────────
        await ConvergeAllAsync(aReplica, bReplica);

        // Both rosters converged on A's genesis, both validate to it.
        Assert.True(aReplica.NodeRoster.Current.Contains(b.PartyId), "A's roster contains B (A admitted B).");
        Assert.True(bReplica.NodeRoster.Current.Contains(a.PartyId), "B's adopted roster contains A.");
        Assert.True(bReplica.NodeRoster.Current.ValidatesToGenesis(Verifier), "B's adopted roster validates to A's genesis.");
        Assert.Equal(a.PartyId, bReplica.NodeRoster.Current.GenesisPartyId);

        // ── THE DECISION (post-rebuild): A↔B transport trust must SURVIVE the converged-roster rebuild. ─────────
        var policyA = TrustPolicyFor(aReplica.NodeRoster, aATeamKey);
        var policyB = TrustPolicyFor(bReplica.NodeRoster, bATeamKey);

        // A trusts B's A-team HELLO — the EXACT decision A's /sync-status reported as PEER_UNTRUSTED cross-device.
        Assert.True(policyA.IsTrusted(HelloFor(b.TransportIdentity(aTeam))),
            "A must STILL trust B's A-team HELLO after the converged-roster rebuild — the cross-device PEER_UNTRUSTED "
            + "the verify hit. The transport-trust set must FOLLOW the adopted team through AdoptSyncedRoster (the "
            + "bug-1332 sibling), not regress to distrust.");
        Assert.Contains(aReplica.NodeRoster.TrustedTransportKeys(), k => k.AsSpan().SequenceEqual(bATeamKey));

        // B trusts A's A-team HELLO (the mirror the joiner side reported).
        Assert.True(policyB.IsTrusted(HelloFor(a.TransportIdentity(aTeam))),
            "B must STILL trust A's A-team HELLO after the rebuild (the mirror of A's PEER_UNTRUSTED).");
        Assert.Contains(bReplica.NodeRoster.TrustedTransportKeys(), k => k.AsSpan().SequenceEqual(aATeamKey));

        // Never-brick floor: each side trusts its own A-team HELLO.
        Assert.True(policyA.IsTrusted(HelloFor(a.TransportIdentity(aTeam))), "A trusts itself (own-subkey floor).");
        Assert.True(policyB.IsTrusted(HelloFor(b.TransportIdentity(aTeam))), "B trusts itself (own-subkey floor).");
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════
    // THE REPRO OVER A REAL WIRE: the SAME joiner lifecycle, but the trust is proven by a REAL TCP handshake (the
    // exact MemberSetTrustPolicy HELLO gate that emits PEER_UNTRUSTED on A's /sync-status cross-device), not just a
    // policy-snapshot assertion. A LISTENS; B dials A after the converged-roster rebuild → the handshake must be
    // TRUSTED + a roster delta crosses A↔B (if the transport trust had regressed, this is exactly PEER_UNTRUSTED).
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════

    [Fact(DisplayName = "cross-device adopt OVER A REAL WIRE: after the converged-roster rebuild, B dials A over a real TCP socket and the MemberSetTrustPolicy HELLO gate PASSES (a roster delta crosses A↔B) — NOT the PEER_UNTRUSTED the cross-device verify hit")]
    public async Task Joiner_OwnGenesis_RealWire_Handshake_Trusted_After_Rebuild()
    {
        var a = Node.New("os:A#founder", ownTeamSeed: "team-A-office");
        var b = Node.New("os:B#bob", ownTeamSeed: "team-B-home");
        var aTeam = a.OwnTeam;

        var aReplica = await NewReplicaAsync("A", GenesisFor(a, aTeam), a.PrincipalSigner);
        await SeedOwnGenesisAsync(aReplica, a, aTeam);
        var bReplica = await NewReplicaAsync("B", GenesisFor(b, b.OwnTeam), b.PrincipalSigner);
        await SeedOwnGenesisAsync(bReplica, b, b.OwnTeam);

        await AdmitAndPublishAsync(aReplica, a, aTeam, b);
        bReplica.NodeRoster.AdoptEnrollment(
            aReplica.NodeRoster.Current,
            new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                [a.PartyId] = a.TransportPublicKey(aTeam),
                [b.PartyId] = b.TransportPublicKey(aTeam),
            });
        await bReplica.Projection.SupersedeOwnTeamRecordsAsync(b.OwnTeam, CancellationToken.None);
        await bReplica.Projection.DrainPendingReconcilesAsync();
        await ConvergeAllAsync(aReplica, bReplica);

        // Each node presents its A-team-scoped transport identity on the wire HELLO (the #1296-F2 joined-team scope).
        var aDevice = a.TransportIdentity(aTeam);
        var bDevice = b.TransportIdentity(aTeam);
        Assert.NotEqual(aDevice.PublicKey, bDevice.PublicKey);

        // A LISTENS; B DIALS A. The trust gate on each side is the REAL MemberSetTrustPolicy over the CONVERGED
        // roster's transport keys (rebuilt by AdoptSyncedRoster) — the exact gate A's /sync-status read as
        // PEER_UNTRUSTED cross-device.
        var aSigner = new Ed25519Signer();
        var transportAListen = new TcpSyncDaemonTransport("tcp://127.0.0.1:0");
        _cleanup.Add(transportAListen);
        var endpointA = transportAListen.ListenEndpoint!;
        var daemonA = BuildDaemon(transportAListen, aDevice, aSigner, aReplica.Projection,
            TrustPolicyFor(aReplica.NodeRoster, a.TransportPublicKey(aTeam)));
        _cleanup.Add(daemonA);
        await daemonA.StartListeningAsync(CancellationToken.None);

        var bSigner = new Ed25519Signer();
        var transportBOut = new TcpSyncDaemonTransport();
        _cleanup.Add(transportBOut);
        var daemonB = BuildDaemon(transportBOut, bDevice, bSigner, bReplica.Projection,
            TrustPolicyFor(bReplica.NodeRoster, b.TransportPublicKey(aTeam)));
        _cleanup.Add(daemonB);
        daemonB.AddPeer(endpointA, aDevice.PublicKey);
        await daemonB.StartAsync(CancellationToken.None);

        // PROOF the A↔B session is TRUSTED + converges: A admits a third member C, publishes it to A's doctype ONLY
        // (NOT synced to B). The DIRECT B↔A roster-sync session must carry C's admission to B — which only happens
        // if the B→A handshake passes the trust gate. If it were PEER_UNTRUSTED (the cross-device wall) C never
        // arrives.
        var carol = Node.New("os:C#carol", ownTeamSeed: "team-C-misc");
        await AdmitAndPublishAsync(aReplica, a, aTeam, carol);
        Assert.True(aReplica.NodeRoster.Current.Contains(carol.PartyId), "A holds C's admission (A admitted C).");
        Assert.False(bReplica.NodeRoster.Current.Contains(carol.PartyId), "B does NOT yet hold C (no sync since).");

        await WaitForRosterMemberAsync(bReplica, carol.PartyId,
            because: "after the joiner adopted A's team and the roster converged, the REAL B↔A handshake must be "
                + "TRUSTED (MemberSetTrustPolicy passes B's A-team HELLO) so the roster doctype crosses and C's "
                + "admission reaches B — NOT the cross-device PEER_UNTRUSTED.");

        await daemonB.StopAsync(CancellationToken.None);
        await daemonA.StopListeningAsync(CancellationToken.None);
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════
    // REFUTE (security): the fix must NOT flip a legitimate distrust into a wrongful trust. A stranger that NEVER
    // enrolled into A's team STAYS PEER_UNTRUSTED after the same converged-roster rebuild — even one that knows
    // A's team id and derives a real A-team-scoped key. Trust is ENROLLMENT, not knowing the team id.
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════

    [Fact(DisplayName = "cross-device REFUTE (security): a NON-enrolled stranger (knows A's team id, derives a real A-team key, but A never admitted it) STAYS PEER_UNTRUSTED after the same converged-roster rebuild — the fix scopes trust to a LEGITIMATELY-ADOPTED team, it does not trust anyone claiming the team")]
    public async Task NonEnrolled_Stranger_Stays_PeerUntrusted_After_Rebuild()
    {
        var a = Node.New("os:A#founder", ownTeamSeed: "team-A-office");
        var b = Node.New("os:B#bob", ownTeamSeed: "team-B-home");
        var mallory = Node.New("os:M#mallory", ownTeamSeed: "team-A-office"); // even with A's team SEED — never admitted.
        var aTeam = a.OwnTeam;

        var aReplica = await NewReplicaAsync("A", GenesisFor(a, aTeam), a.PrincipalSigner);
        await SeedOwnGenesisAsync(aReplica, a, aTeam);
        var bReplica = await NewReplicaAsync("B", GenesisFor(b, b.OwnTeam), b.PrincipalSigner);
        await SeedOwnGenesisAsync(bReplica, b, b.OwnTeam);

        // A admits ONLY B (mallory is never admitted — no invite, no signed admission).
        await AdmitAndPublishAsync(aReplica, a, aTeam, b);
        bReplica.NodeRoster.AdoptEnrollment(
            aReplica.NodeRoster.Current,
            new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                [a.PartyId] = a.TransportPublicKey(aTeam),
                [b.PartyId] = b.TransportPublicKey(aTeam),
            });
        await bReplica.Projection.SupersedeOwnTeamRecordsAsync(b.OwnTeam, CancellationToken.None);
        await bReplica.Projection.DrainPendingReconcilesAsync();
        await ConvergeAllAsync(aReplica, bReplica);

        // Mallory derives a GENUINE A-team-scoped transport key (she knows A's team id) — but A never signed her
        // into the roster, so her party is not a live member → her transport key is NEVER harvested into any trust
        // set. The rebuild is keyed on the SIGNED chain, not on knowing the team id.
        var malloryATeamKey = mallory.TransportPublicKey(aTeam);
        var policyA = TrustPolicyFor(aReplica.NodeRoster, a.TransportPublicKey(aTeam));
        var policyB = TrustPolicyFor(bReplica.NodeRoster, b.TransportPublicKey(aTeam));

        Assert.False(aReplica.NodeRoster.Current.Contains(mallory.PartyId), "A never admitted mallory.");
        Assert.False(bReplica.NodeRoster.Current.Contains(mallory.PartyId), "mallory is in no converged roster.");
        Assert.False(policyA.IsTrusted(HelloFor(mallory.TransportIdentity(aTeam))),
            "a non-enrolled stranger STAYS PEER_UNTRUSTED on A — the fix scopes trust to a LEGITIMATELY-ADOPTED "
            + "team (a signed admission), NOT to anyone who can derive an A-team key.");
        Assert.False(policyB.IsTrusted(HelloFor(mallory.TransportIdentity(aTeam))),
            "the stranger stays PEER_UNTRUSTED on B too (the C5 by-association rule holds through the rebuild).");
        Assert.DoesNotContain(aReplica.NodeRoster.TrustedTransportKeys(), k => k.AsSpan().SequenceEqual(malloryATeamKey));
        Assert.DoesNotContain(bReplica.NodeRoster.TrustedTransportKeys(), k => k.AsSpan().SequenceEqual(malloryATeamKey));

        // The legitimate trust is intact (B still trusted on A; A still trusted on B) — no brick from the refute.
        Assert.True(policyA.IsTrusted(HelloFor(b.TransportIdentity(aTeam))), "A still trusts the real member B.");
        Assert.True(policyB.IsTrusted(HelloFor(a.TransportIdentity(aTeam))), "B still trusts A.");
    }
}
