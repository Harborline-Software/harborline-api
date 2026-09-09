using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

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
/// THE ≥3-NODE MESH PROOF (INFO-2 / the transport-key-in-synced-record follow-on). The flagged gap: the synced
/// roster record carried the member's PRINCIPAL key (forge-proof attribution converges mesh-wide) but NOT its
/// team-scoped TRANSPORT key (the sync-HELLO key <see cref="MemberSetTrustPolicy"/> checks). The transport map was
/// wired ONLY locally — by the admitter (<see cref="NodeTeamRoster.AdmitPeer"/>) and the joiner's one-time
/// <see cref="NodeTeamRoster.AdoptEnrollment"/>; <see cref="NodeTeamRoster.AdoptSyncedRoster"/> could only PRUNE,
/// never ADD. So in a star-admitted team A→{B,C} where B and C are admitted at different times, B never learned
/// C's transport key (and vice-versa) → B↔C could not pass the trust gate → only the A-hub worked
/// (PEER_UNTRUSTED on B↔C).
/// <para>
/// THE FIX: the team-scoped transport PUBLIC key now RIDES the synced admission record
/// (<c>RosterRecordCrdtState.TransportPublicKeyB64Url</c>); <see cref="RosterCrdtProjection.RebuildLiveRoster"/>
/// harvests it for every genesis-validated live member and <see cref="NodeTeamRoster.AdoptSyncedRoster"/> REBUILDS
/// the transport map from the converged roster — so every converged member derives the SAME full transport-trust
/// set from the SAME roster, regardless of admit order. The transport set is ROSTER-DERIVED.
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// <b>Real persisted store + real wire (the recurring "test the REAL host/wire/store" lesson).</b> Three
/// independent <see cref="RosterCrdtProjection"/> replicas, each over its OWN <see cref="YDotNetCrdtEngine"/> +
/// its OWN SQLite-persisted <c>roster.db</c> + its OWN <see cref="NodeTeamRoster"/>. The roster CRDT converges
/// over REAL TCP sockets via <see cref="GossipDaemon"/>s + <see cref="TcpSyncDaemonTransport"/> — the same
/// produce/consume path the production daemon runs, with NO signal-bridge relay (local-first). The trust
/// decisions under test are the real <see cref="MemberSetTrustPolicy"/> over the REAL team-scoped transport
/// subkeys (HKDF(member-root, A.teamId)), computed from the CONVERGED roster — never hand-seeded.
/// </para>
/// <para>
/// <b>NOT hand-provisioned (the lesson of the whole enrollment arc).</b> B and C learn each other's transport key
/// ONLY because the synced roster record carries it and the rebuild harvests it. The
/// <see cref="Mesh_BC_Trust_Converges_Via_Synced_Roster_Not_Hub"/> headline runs the SAME body with the fix
/// neutered (records published WITHOUT the carried key) and asserts B↔C does NOT converge — proving the test
/// exercises the new carried-key path, not a pre-existing one.
/// </para>
/// </remarks>
public sealed class ThreeNodeMeshTransportKeyTests : IAsyncLifetime
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

    private static readonly Guid Team = Guid.Parse("7e57aaaa-0000-0000-0000-00000000000a");
    private static readonly string TeamIdString = Team.ToString("D");
    private static readonly IOperationVerifier Verifier = new Ed25519Verifier();
    private static readonly IEd25519Signer TransportSigner = new Ed25519Signer();
    private static readonly ITeamSubkeyDerivation SubkeyDerivation = new TeamSubkeyDerivation(TransportSigner);

    // ───────────────────────────────────────────────────────────────────────────────────────────────────────
    // A team member: a comms/principal identity (the roster binding) + a distinct node ROOT (the source of its
    // team-scoped transport subkey — material no other node holds), like two distinct installs.
    // ───────────────────────────────────────────────────────────────────────────────────────────────────────
    private sealed record Member(string PartyId, KeyPair PrincipalKey, IOperationSigner PrincipalSigner, NodeIdentity Root)
    {
        public static Member New(string partyId)
        {
            var kp = KeyPair.Generate();
            var rootSeed = new byte[32];
            System.Security.Cryptography.RandomNumberGenerator.Fill(rootSeed);
            var (rootPub, rootPriv) = TransportSigner.GenerateFromSeed(rootSeed);
            var nodeId = Convert.ToHexString(rootPub.AsSpan(0, 16)).ToLowerInvariant();
            var root = new NodeIdentity(nodeId, rootPub, rootPriv);
            return new Member(partyId, kp, new Harborline.Api.Foundation.Crypto.Ed25519Signer(kp), root);
        }

        /// <summary>The member's team-scoped TRANSPORT identity for A's team (HKDF(root-private, A.teamId)) — what
        /// it presents in the sync HELLO and what the trust gate must trust.</summary>
        public NodeIdentity TransportIdentity =>
            TeamScopedNodeIdentity.Derive(Root, TeamIdString, SubkeyDerivation);

        /// <summary>The raw 32-byte transport PUBLIC key the joiner supplies over the admission channel — the
        /// datum that now rides the synced roster record.</summary>
        public byte[] TransportPublicKey => TransportIdentity.PublicKey;
    }

    // ── A roster-sync replica: own CRDT engine, own persisted store, own live roster + projection ──────────
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
        var dir = Path.Combine(Path.GetTempPath(), $"harborline-3node-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        var connectionString = $"Data Source={Path.Combine(dir, "roster.db")};Pooling=False";

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContextFactory<NodeLocalRosterDbContext>(opt => opt.UseSqlite(connectionString));
        services.AddSingleton<ICrdtEngine, YDotNetCrdtEngine>();

        var sp = services.BuildServiceProvider();
        var factory = sp.GetRequiredService<IDbContextFactory<NodeLocalRosterDbContext>>();
        // EnsureCreated builds the schema from the CURRENT model — including the new transport_public_key column
        // (the AdditiveMigration test below covers the column-add against an OLD-schema db explicitly).
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
        _replicas_addCleanup(replica);
        return replica;
    }

    private readonly List<Replica> _replicas = new();
    private void _replicas_addCleanup(Replica r) { _replicas.Add(r); _cleanup.Add(r); }

    private static MemberRoster GenesisFor(Member founder) =>
        MemberRoster.StableGenesis(Team, founder.PartyId, founder.PrincipalSigner, Verifier);

    /// <summary>Seed the founder's genesis self-admission onto the synced doctype, CARRYING the founder's own
    /// team-scoped transport key (what RosterSyncBootstrapHostedService does in production).</summary>
    private static async Task SeedGenesisAsync(Replica r, Member founder) =>
        await r.Projection.PublishLocalAsync(
            RosterRecordCrdtState.FromAdmission(
                r.NodeRoster.Current.EnumerateAdmissions().Single(), founder.TransportPublicKey),
            CancellationToken.None);

    /// <summary>
    /// Admit <paramref name="joiner"/> on the founder replica (the A-side admit), update A's live roster + record
    /// the joiner's transport key locally (AdmitPeer), and publish the admission record CARRYING the joiner's
    /// transport key — exactly what <see cref="WireEnrollmentAdmitter"/> does. <paramref name="carryTransportKey"/>
    /// false = the pre-fix counterfactual (publish WITHOUT the carried key).
    /// </summary>
    private static async Task AdmitAndPublishAsync(
        Replica a, Member founder, Member joiner, bool carryTransportKey = true)
    {
        var withJoiner = a.NodeRoster.Current.Admit(
            founder.PartyId, founder.PrincipalSigner, joiner.PartyId, joiner.PrincipalKey.PrincipalId,
            PermissionCompositions.Member, Verifier, DateTimeOffset.UtcNow, Guid.NewGuid());
        a.NodeRoster.AdmitPeer(withJoiner, joiner.PartyId, joiner.TransportPublicKey);
        var rec = withJoiner.EnumerateAdmissions().Single(x => x.PartyId == joiner.PartyId);
        await a.Projection.PublishLocalAsync(
            RosterRecordCrdtState.FromAdmission(rec, carryTransportKey ? joiner.TransportPublicKey : null),
            CancellationToken.None);
    }

    /// <summary>One direction of a roster sync round over the projections (encode src delta vs dst clock → apply →
    /// drain the reconcile the Changed handler spawns). Mirrors the gossip daemon's produce/consume.</summary>
    private static async Task SyncDirectAsync(Replica src, Replica dst)
    {
        var delta = await src.Projection.EncodeOutboundDeltaAsync(
            RosterCrdtProjection.DocumentId, dst.Projection.VectorClock, CancellationToken.None);
        Assert.NotNull(delta);
        await dst.Projection.ApplyInboundDeltaAsync(
            RosterCrdtProjection.DocumentId, 1, delta!.Value, CancellationToken.None);
        await dst.Projection.DrainPendingReconcilesAsync();
    }

    /// <summary>Converge the roster fully across all three replicas (every pair, both directions, until stable) —
    /// CRDT convergence is order-independent, so a few rounds reach the fixpoint.</summary>
    private static async Task ConvergeAllAsync(params Replica[] replicas)
    {
        for (var round = 0; round < 3; round++)
        {
            foreach (var src in replicas)
                foreach (var dst in replicas)
                    if (!ReferenceEquals(src, dst))
                        await SyncDirectAsync(src, dst);
        }
        // The PRODUCTION periodic anti-entropy round re-reconciles each node's converged list (push-on-change is
        // the primary path; the periodic round is the backstop). Model that backstop here so a converged backfill
        // replace (delete+insert of an already-present record) whose Changed-trigger was coalesced during a busy
        // round is still reconciled — deterministic test convergence matching production's eventual consistency.
        foreach (var r in replicas)
        {
            await r.Projection.ReconcileAsync(CancellationToken.None);
            await r.Projection.DrainPendingReconcilesAsync();
        }
    }

    /// <summary>The trust gate the per-team registrar builds: {own subkey floor} ∪ {the live roster's
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

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════
    // THE HEADLINE: a 3-node mesh — B↔C trust converges via the SYNCED roster, not via the A-hub.
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════

    [Theory(DisplayName = "≥3-node mesh: A admits B then C; after the roster CRDT converges, B and C MUTUALLY trust each other's transport key (B↔C) derived from the converged roster — NOT via the A-hub (fix carries the key; pre-fix does not)")]
    [InlineData(true)]   // FIX: the synced record carries the transport key → B↔C converge.
    [InlineData(false)]  // PRE-FIX counterfactual: no carried key → B↔C do NOT converge (only the A-hub works).
    public async Task Mesh_BC_Trust_Converges_Via_Synced_Roster_Not_Hub(bool carryTransportKey)
    {
        var founder = Member.New("os:A#founder");
        var bob = Member.New("os:B#bob");
        var carol = Member.New("os:C#carol");

        // Three INDEPENDENT, persisted replicas. A founds; B and C adopt A's team genesis as their trust root (the
        // joiner posture — they do NOT publish a competing genesis, so the team has exactly one genesis).
        var a = await NewReplicaAsync("A", GenesisFor(founder), founder.PrincipalSigner);
        await SeedGenesisAsync(a, founder);
        var aGenesisRoster = a.NodeRoster.Current;
        var b = await NewReplicaAsync("B", aGenesisRoster, founder.PrincipalSigner);
        var c = await NewReplicaAsync("C", aGenesisRoster, founder.PrincipalSigner);

        // A admits B, THEN (later) A admits C — the star-admission shape. Each admission publishes A's record
        // carrying (or, pre-fix, omitting) the admitted peer's transport key.
        await AdmitAndPublishAsync(a, founder, bob, carryTransportKey);
        await AdmitAndPublishAsync(a, founder, carol, carryTransportKey);

        // The roster CRDT converges across all three (over the projections' produce/consume — the daemon path).
        await ConvergeAllAsync(a, b, c);

        // ── Membership + attribution converge mesh-wide in BOTH cases (principal keys always rode the record). ──
        Assert.True(b.NodeRoster.Current.Contains(carol.PartyId), "B's converged roster must contain C (membership).");
        Assert.True(c.NodeRoster.Current.Contains(bob.PartyId), "C's converged roster must contain B (membership).");
        Assert.True(b.NodeRoster.Current.ValidatesToGenesis(Verifier));
        Assert.True(c.NodeRoster.Current.ValidatesToGenesis(Verifier));

        // ── THE TRUST DECISION: does B trust C's wire HELLO (and vice-versa), computed from the CONVERGED roster? ──
        // The own-subkey floor is each node's OWN A-team transport key (what the registrar contributes).
        var policyB = TrustPolicyFor(b.NodeRoster, bob.TransportPublicKey);
        var policyC = TrustPolicyFor(c.NodeRoster, carol.TransportPublicKey);

        var bTrustsC = policyB.IsTrusted(HelloFor(carol.TransportIdentity));
        var cTrustsB = policyC.IsTrusted(HelloFor(bob.TransportIdentity));

        if (carryTransportKey)
        {
            // THE FIX: B↔C MUTUALLY trust each other DIRECTLY — the ≥3-node mesh works without the A-hub.
            Assert.True(bTrustsC,
                "FIX: B must trust C's transport key, HARVESTED from the converged roster record C's admission "
                + "carried — the ≥3-node mesh property. Pre-fix this was PEER_UNTRUSTED (only the A-hub worked).");
            Assert.True(cTrustsB,
                "FIX: C must trust B's transport key (B was admitted BEFORE C, yet C harvests B's key from the "
                + "converged roster — no admit-order dependency).");
            // #1296-F2 reinforcement: the trusted key is the JOINED-team-scoped subkey (HKDF(member-root, A.teamId)).
            Assert.Contains(b.NodeRoster.TrustedTransportKeys(),
                k => k.AsSpan().SequenceEqual(carol.TransportPublicKey));
            Assert.Contains(c.NodeRoster.TrustedTransportKeys(),
                k => k.AsSpan().SequenceEqual(bob.TransportPublicKey));
        }
        else
        {
            // PRE-FIX counterfactual: the record carried NO transport key → the rebuild harvested nothing → B↔C
            // CANNOT trust each other. This is the exact INFO-2 failure (only the A-hub works). Proves the
            // headline above exercises the new carried-key path, not a pre-existing one.
            Assert.False(bTrustsC,
                "PRE-FIX: without the carried transport key, B has no way to learn C's transport key from the "
                + "synced roster → B↔C is PEER_UNTRUSTED (the INFO-2 gap — only the A-hub worked).");
            Assert.False(cTrustsB, "PRE-FIX: symmetric — C cannot trust B either.");
        }

        // ── In BOTH cases the never-brick floor holds: every node trusts itself + the A-hub stays trusted. ──
        Assert.True(policyB.IsTrusted(HelloFor(bob.TransportIdentity)), "B trusts itself (own-subkey floor).");
        Assert.True(policyB.IsTrusted(HelloFor(founder.TransportIdentity)), "B trusts the A-hub (admitter).");
        Assert.True(policyC.IsTrusted(HelloFor(carol.TransportIdentity)), "C trusts itself (own-subkey floor).");
        Assert.True(policyC.IsTrusted(HelloFor(founder.TransportIdentity)), "C trusts the A-hub (admitter).");
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════
    // THE HEADLINE OVER A REAL WIRE: a real B↔C sync session completes (comms cross B↔C directly, signal-bridge
    // STOPPED), proving the roster-derived trust is honored by the actual handshake, not just the policy snapshot.
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════

    [Fact(DisplayName = "≥3-node mesh OVER A REAL WIRE: after roster converges, a B↔C roster sync session completes directly over a TCP socket (NOT via A) — the roster-derived transport trust is honored by the real handshake; no signal-bridge")]
    public async Task Mesh_BC_Sync_Session_Completes_Over_Real_Wire_Direct_Not_Via_Hub()
    {
        var founder = Member.New("os:A#founder");
        var bob = Member.New("os:B#bob");
        var carol = Member.New("os:C#carol");

        var a = await NewReplicaAsync("A", GenesisFor(founder), founder.PrincipalSigner);
        await SeedGenesisAsync(a, founder);
        var b = await NewReplicaAsync("B", a.NodeRoster.Current, founder.PrincipalSigner);
        var c = await NewReplicaAsync("C", a.NodeRoster.Current, founder.PrincipalSigner);

        await AdmitAndPublishAsync(a, founder, bob);
        await AdmitAndPublishAsync(a, founder, carol);
        await ConvergeAllAsync(a, b, c);

        // Each node presents its A-team-scoped transport identity on the wire HELLO (the #1296-F2 joined-team scope).
        var bDevice = TeamScopedNodeIdentity.Derive(bob.Root, TeamIdString, SubkeyDerivation);
        var cDevice = TeamScopedNodeIdentity.Derive(carol.Root, TeamIdString, SubkeyDerivation);
        Assert.NotEqual(bDevice.PublicKey, cDevice.PublicKey);

        // C LISTENS; B DIALS C — a DIRECT B↔C session (A is NOT in this path; no signal-bridge relay). The trust
        // gate on each side is the REAL MemberSetTrustPolicy over the converged roster's transport keys.
        var cSigner = new Ed25519Signer();
        var transportCListen = new TcpSyncDaemonTransport("tcp://127.0.0.1:0");
        _cleanup.Add(transportCListen);
        var endpointC = transportCListen.ListenEndpoint!;
        var daemonC = BuildDaemon(transportCListen, cDevice, cSigner, c.Projection,
            TrustPolicyFor(c.NodeRoster, carol.TransportPublicKey));
        _cleanup.Add(daemonC);
        await daemonC.StartListeningAsync(CancellationToken.None);

        var bSigner = new Ed25519Signer();
        var transportBOut = new TcpSyncDaemonTransport();
        _cleanup.Add(transportBOut);
        var daemonB = BuildDaemon(transportBOut, bDevice, bSigner, b.Projection,
            TrustPolicyFor(b.NodeRoster, bob.TransportPublicKey));
        _cleanup.Add(daemonB);
        daemonB.AddPeer(endpointC, cDevice.PublicKey);
        await daemonB.StartAsync(CancellationToken.None);

        // PROOF the B↔C session is TRUSTED + converges: B has a record C does not (yet). The simplest such record
        // is one bob re-publishes — but the roster is already converged. Instead, prove convergence-by-trust: have
        // B admit nobody, but assert the session reaches a converged clock with NO PEER_UNTRUSTED. We do that by
        // making C learn a record only B will push: B publishes a (idempotent) re-seed of carol's record CARRYING
        // its transport key while C's copy is identical, so the session must HANDSHAKE (trust) even if no delta
        // flows. The decisive signal is that the handshake does NOT reject — verified by driving a real new record.
        //
        // Concretely: A admits a fourth member D, publishes to A, syncs A→B ONLY (NOT to C). Now B holds D's
        // admission that C lacks. The DIRECT B↔C session must carry D to C — which only happens if B↔C is TRUSTED.
        var dave = Member.New("os:D#dave");
        await AdmitAndPublishAsync(a, founder, dave);
        await SyncDirectAsync(a, b);                       // B learns D; C does NOT (A→B only).
        Assert.True(b.NodeRoster.Current.Contains(dave.PartyId), "B must hold D's admission (A→B sync).");
        Assert.False(c.NodeRoster.Current.Contains(dave.PartyId), "C must NOT yet hold D (no A→C sync).");

        // Over the LIVE B→C wire session, D's admission must reach C — i.e. the B↔C handshake is TRUSTED + the
        // roster doctype crosses directly. If B↔C were PEER_UNTRUSTED (the pre-fix world) D would never arrive.
        await WaitForRosterMemberAsync(c, dave.PartyId,
            because: "after roster-derived trust converged, the DIRECT B→C session (NOT via A, no signal-bridge) "
                + "must complete its trusted handshake and carry D's admission to C.");

        await daemonB.StopAsync(CancellationToken.None);
        await daemonC.StopListeningAsync(CancellationToken.None);
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════
    // REFUTE 1: a FORGED transport key for a NON-member is NOT trusted (the by-association trust gate bites).
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════

    [Fact(DisplayName = "≥3-node refute: a FORGED transport key carried for a NON-member is NOT trusted — the rebuild harvests transport keys ONLY for genesis-validated live members (UNSIGNED-by-association)")]
    public async Task Forged_Transport_Key_For_NonMember_Is_Not_Trusted()
    {
        var founder = Member.New("os:A#founder");
        var bob = Member.New("os:B#bob");
        var mallory = Member.New("os:M#mallory");   // NEVER admitted by A — a stranger.

        var a = await NewReplicaAsync("A", GenesisFor(founder), founder.PrincipalSigner);
        await SeedGenesisAsync(a, founder);
        var b = await NewReplicaAsync("B", a.NodeRoster.Current, founder.PrincipalSigner);

        await AdmitAndPublishAsync(a, founder, bob);
        await ConvergeAllAsync(a, b);

        // An attacker injects a roster record for mallory carrying mallory's REAL transport key but a FORGED
        // admission (signed by mallory's own non-admin principal key, not by A). Build it by hand the way a
        // bogus delta would: a self-signed "admission" mallory tries to pass off.
        var forgedSelfAdmit = MemberRoster.Genesis(
            Team, mallory.PartyId, mallory.PrincipalSigner, Verifier, DateTimeOffset.UtcNow, Guid.NewGuid());
        var forgedRec = forgedSelfAdmit.EnumerateAdmissions().Single();
        // Stamp mallory's real transport key onto the forged record + publish it onto B's doctype (the inject).
        var forgedWire = RosterRecordCrdtState.FromAdmission(forgedRec, mallory.TransportPublicKey);
        await b.Projection.PublishLocalAsync(forgedWire, CancellationToken.None);
        await b.Projection.DrainPendingReconcilesAsync();

        // The injection guard (FromSyncedRecords: ≥2 genesis → reject; mallory's self-admit is a SECOND genesis)
        // drops the whole rebuild → B keeps its prior valid roster → mallory is NOT a member → her transport key
        // is NOT harvested → the trust gate REJECTS her HELLO. The forged transport key bought nothing.
        var policyB = TrustPolicyFor(b.NodeRoster, bob.TransportPublicKey);
        Assert.False(b.NodeRoster.Current.Contains(mallory.PartyId),
            "the forged self-admission is a second genesis — rejected; mallory never becomes a member.");
        Assert.False(policyB.IsTrusted(HelloFor(mallory.TransportIdentity)),
            "a forged transport key for a NON-member is NEVER trusted — the trust anchor (the signed chain) is "
            + "unchanged; the carried key is honored ONLY for a chain-validated member (by-association).");
        Assert.DoesNotContain(b.NodeRoster.TrustedTransportKeys(),
            k => k.AsSpan().SequenceEqual(mallory.TransportPublicKey));
        // B's legitimate trust is intact (bob still trusted; the inject did not brick B).
        Assert.True(policyB.IsTrusted(HelloFor(bob.TransportIdentity)));
        Assert.True(policyB.IsTrusted(HelloFor(founder.TransportIdentity)));
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════
    // REFUTE 2: a REVOKED member's transport key IS DROPPED post-converge (gap-#3 preserved — rebuild-from-live
    // subsumes the prune). Confirms the new rebuild does not REGRESS the revocation-drop the prior increment fixed.
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════

    [Fact(DisplayName = "≥3-node refute: a REVOKED member's transport key is DROPPED post-converge — rebuild-from-live subsumes the gap-#3 prune (no regression)")]
    public async Task Revoked_Member_Transport_Key_Is_Dropped_Post_Converge()
    {
        var founder = Member.New("os:A#founder");
        var bob = Member.New("os:B#bob");
        var carol = Member.New("os:C#carol");

        var a = await NewReplicaAsync("A", GenesisFor(founder), founder.PrincipalSigner);
        await SeedGenesisAsync(a, founder);
        var c = await NewReplicaAsync("C", a.NodeRoster.Current, founder.PrincipalSigner);

        // A admits B and C, both carry transport keys; converge → C trusts B (the mesh fix).
        await AdmitAndPublishAsync(a, founder, bob);
        await AdmitAndPublishAsync(a, founder, carol);
        await ConvergeAllAsync(a, c);

        var policyC = TrustPolicyFor(c.NodeRoster, carol.TransportPublicKey);
        Assert.True(policyC.IsTrusted(HelloFor(bob.TransportIdentity)),
            "pre-revocation: C trusts B's transport key (harvested from the converged roster).");

        // A REVOKES B and publishes the signed revocation; it syncs + converges to C.
        var (afterRevoke, signedRevocation) = a.NodeRoster.Current.SignRevoke(
            founder.PartyId, founder.PrincipalSigner, bob.PartyId, Verifier, DateTimeOffset.UtcNow, Guid.NewGuid());
        a.NodeRoster.AdoptSyncedRoster(afterRevoke);
        await a.Projection.PublishLocalAsync(
            RosterRecordCrdtState.FromRevocation(signedRevocation), CancellationToken.None);
        await ConvergeAllAsync(a, c);

        // POST-convergence: B is dropped from C's principal roster (attribution gone) AND — because the rebuild is
        // keyed on the CONVERGED LIVE-MEMBER set — B's transport key is NOT rebuilt (B is no longer a live member).
        // So the gap-#3 revocation-drop is PRESERVED by the new rebuild (rebuild-from-live subsumes the prune).
        Assert.False(c.NodeRoster.Current.Contains(bob.PartyId), "B's attribution binding is dropped on C.");
        Assert.Null(c.NodeRoster.ForgeProofBinding(bob.PartyId));
        Assert.False(policyC.IsTrusted(HelloFor(bob.TransportIdentity)),
            "THE gap-#3 PROPERTY (preserved): a revoked member's transport key is dropped → C REJECTS B's HELLO "
            + "post-converge. The roster-derived rebuild reconstructs only live members, so a revoked member's key "
            + "is never rebuilt.");
        Assert.DoesNotContain(c.NodeRoster.TrustedTransportKeys(),
            k => k.AsSpan().SequenceEqual(bob.TransportPublicKey));
        // C is not bricked: it still trusts itself + the founder.
        Assert.True(policyC.IsTrusted(HelloFor(carol.TransportIdentity)));
        Assert.True(policyC.IsTrusted(HelloFor(founder.TransportIdentity)));
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════
    // ADDITIVE MIGRATION: an OLD-schema record (no transport field) still works — back-compat 2-node hub.
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════

    [Fact(DisplayName = "additive migration: an OLD admission record carrying NO transport key still converges + works (the 2-node hub via the legacy AdmitPeer/floor path); a mixed old/new record set rebuilds correctly")]
    public async Task Additive_Migration_Old_Record_Without_Transport_Key_Still_Works()
    {
        var founder = Member.New("os:A#founder");
        var bob = Member.New("os:B#bob");
        var carol = Member.New("os:C#carol");

        var a = await NewReplicaAsync("A", GenesisFor(founder), founder.PrincipalSigner);
        // The genesis carries the founder's own transport key (the production seed path — the upgraded founder).
        await SeedGenesisAsync(a, founder);
        var b = await NewReplicaAsync("B", a.NodeRoster.Current, founder.PrincipalSigner);
        var c = await NewReplicaAsync("C", a.NodeRoster.Current, founder.PrincipalSigner);

        // MIXED record set: B admitted with an OLD record (no carried key), C admitted with a NEW record (carries).
        // This is the realistic rollout shape — an upgraded founder (genesis carries) with a member admitted by a
        // pre-upgrade build (B's record carries nothing) alongside a post-upgrade admission (C's record carries).
        await AdmitAndPublishAsync(a, founder, bob, carryTransportKey: false);   // OLD-style record.
        await AdmitAndPublishAsync(a, founder, carol, carryTransportKey: true);  // NEW-style record.
        await ConvergeAllAsync(a, b, c);

        // Both converge as MEMBERS in all rosters (membership/attribution always rode the principal key — old or new).
        Assert.True(b.NodeRoster.Current.Contains(carol.PartyId));
        Assert.True(c.NodeRoster.Current.Contains(bob.PartyId));
        Assert.True(b.NodeRoster.Current.ValidatesToGenesis(Verifier));
        Assert.True(c.NodeRoster.Current.ValidatesToGenesis(Verifier));

        // The mesh works for the member whose record CARRIED a key (C): B trusts C (harvested from C's new record).
        var policyB = TrustPolicyFor(b.NodeRoster, bob.TransportPublicKey);
        Assert.True(policyB.IsTrusted(HelloFor(carol.TransportIdentity)),
            "the NEW record's carried key still works in a mixed set (additive — new records carry, old ignore).");
        // The OLD record (B's) carried no key → C cannot harvest B's transport key from the roster. This is the
        // documented rollout window: an OLD record contributes nothing to the transport map until it re-emits. It
        // does NOT brick anything — the 2-node hub (A↔B) still works via the live AdmitPeer/floor path, and a
        // re-emit (the founder backfill) carries B's key. Assert the back-compat invariant: no exception, valid
        // roster, C trusts itself + the A-hub (never bricked by the old record).
        var policyC = TrustPolicyFor(c.NodeRoster, carol.TransportPublicKey);
        Assert.True(policyC.IsTrusted(HelloFor(carol.TransportIdentity)), "C trusts itself (floor) — not bricked.");
        Assert.True(policyC.IsTrusted(HelloFor(founder.TransportIdentity)), "C trusts the A-hub — not bricked.");

        // BACKFILL proof: the founder re-emits B's record CARRYING the transport key (replace-in-place); it
        // converges → C now harvests B's key → the mesh completes for B too (the rollout window closes).
        var bRec = a.NodeRoster.Current.EnumerateAdmissions().Single(x => x.PartyId == bob.PartyId);
        await a.Projection.PublishLocalAsync(
            RosterRecordCrdtState.FromAdmission(bRec, bob.TransportPublicKey), CancellationToken.None);
        await ConvergeAllAsync(a, b, c);
        Assert.True(policyC.IsTrusted(HelloFor(bob.TransportIdentity)),
            "after the founder backfills B's record with the transport key (replace-in-place), it converges and C "
            + "harvests B's key → the rollout window closes and the full mesh works.");
    }

    // ── daemon helper (mirrors the socket-E2E template) ──────────────────────────────────────────────────────

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
}
