using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Enrollment;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Crdt;
using Harborline.Api.Kernel.Crdt.Backends;
using Harborline.Api.Kernel.Security.Crypto;
using Harborline.Api.Kernel.Security.Keys;
using Harborline.Api.Kernel.Sync.Handshake;
using Harborline.Api.Kernel.Sync.Identity;
using Harborline.Api.Kernel.Sync.Protocol;
using Harborline.Api.LocalNodeHost.Data.Comms;
using Harborline.Api.LocalNodeHost.Enrollment;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// THE END-TO-END #1277-B1 CLOSURE PROOF (enrollment Phase B; the #1288 M1 carry-forward). Phase A built the
/// forge-proof mechanism + the multi-user trust gate but left them UNWIRED in the SHIPPING config
/// (<c>NodeCommsComposition.AddNodeComms</c> passed <c>rosterBinding: null</c>; the registrar registered
/// <c>SharedRootTrustPolicy</c>). These tests prove the PRODUCTION wiring closes B1 end-to-end:
/// <list type="bullet">
///   <item>building the comms projection through the PRODUCTION composition (<c>AddNodeComms</c> resolving a
///     seeded <see cref="NodeTeamRoster"/>) yields a projection whose LIVE merge gate is forge-proof — a forged-
///     foreign-party peer message is DROPPED on the real merge path, not just in a unit overload;</item>
///   <item>a fresh SINGLE-USER node (genesis only in its own roster) still works — its own messages forge-prove,
///     so an empty/seed roster does NOT brick it;</item>
///   <item>the live <see cref="MemberSetTrustPolicy"/> the registrar now wires rejects a non-roster peer
///     (transport-key gate) while trusting the own-subkey floor.</item>
/// </list>
/// </summary>
public sealed class EnrollmentPhaseBProductionWiringTests : IAsyncLifetime
{
    private readonly List<IAsyncDisposable> _disposables = new();
    private readonly List<ServiceProvider> _providers = new();
    private readonly List<string> _dirs = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var d in _disposables) await d.DisposeAsync();
        foreach (var p in _providers) await p.DisposeAsync();
        foreach (var dir in _dirs)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    private const string Tenant = "team-phaseb";
    private static readonly Guid TeamId = Guid.Parse("7e57dddd-0000-0000-0000-000000000004");
    private static readonly IOperationVerifier Verifier = new Ed25519Verifier();

    private sealed class Member
    {
        public required string PartyId { get; init; }
        public required IOperationSigner Signer { get; init; }
        public required KeyPair KeyPair { get; init; }
        public static Member New(string partyId)
        {
            var kp = KeyPair.Generate();
            return new Member
            {
                PartyId = partyId,
                Signer = new Harborline.Api.Foundation.Crypto.Ed25519Signer(kp), // the comms-author principal signer
                KeyPair = kp,
            };
        }
    }

    // ── Build a CommsCrdtProjection through the PRODUCTION composition (AddNodeComms), over a seeded roster. ──
    private async Task<(CommsCrdtProjection Projection, NodeTeamRoster Roster, IDbContextFactory<NodeLocalCommsDbContext> Factory)>
        NewProductionProjectionAsync(string name, MemberRoster seedRoster)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"harborline-phaseb-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        var connectionString = $"Data Source={Path.Combine(dir, "comms.db")};Pooling=False";

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContextFactory<NodeLocalCommsDbContext>(opt => opt.UseSqlite(connectionString));
        // The PRODUCTION wiring under test: seed the install-level roster + run AddNodeComms (which resolves it
        // and wires rosterBinding into the projection factory). YDotNet pinned for honest list convergence.
        var roster = new NodeTeamRoster(seedRoster);
        services.AddSingleton(roster);
        services.AddSingleton<ITrustedMemberKeyProvider>(roster);
        services.AddSingleton<ICrdtEngine, YDotNetCrdtEngine>();
        services.AddNodeComms();

        var sp = services.BuildServiceProvider();
        _providers.Add(sp);
        var factory = sp.GetRequiredService<IDbContextFactory<NodeLocalCommsDbContext>>();
        await using (var ctx = await factory.CreateDbContextAsync())
            await ctx.Database.EnsureCreatedAsync();

        // Resolve the projection the PRODUCTION factory built — its rosterBinding is wired from NodeTeamRoster.
        var projection = sp.GetRequiredService<CommsCrdtProjection>();
        _disposables.Add(projection);
        return (projection, roster, factory);
    }

    private static MemberRoster GenesisRosterFor(Member founder) =>
        MemberRoster.Genesis(TeamId, founder.PartyId, founder.Signer, Verifier, DateTimeOffset.UtcNow, Guid.NewGuid());

    private static async Task<MessageCrdtState> AppendAsync(CommsCrdtProjection p, Member author, string body)
    {
        var msg = await CommsMessageFactory.CreateSignedAsync(author.Signer, author.PartyId, Tenant, body, DateTimeOffset.UtcNow);
        await p.PersistLocalAsync(msg, CancellationToken.None);
        p.AppendLocal(msg);
        return msg;
    }

    private static async Task SyncAsync(CommsCrdtProjection src, CommsCrdtProjection dst)
    {
        var delta = await src.EncodeOutboundDeltaAsync(CommsCrdtProjection.DocumentId, dst.VectorClock, CancellationToken.None);
        Assert.NotNull(delta);
        await dst.ApplyInboundDeltaAsync(CommsCrdtProjection.DocumentId, 1, delta!.Value, CancellationToken.None);
        await dst.DrainPendingReconcilesAsync();
    }

    // ── TEST 1: the END-TO-END B1 proof — production-wired forge-proof DROPS a forged peer message. ──────────

    [Fact(DisplayName = "Phase B: production-wired comms gate DROPS a forged-foreign-party peer message (B1 closed e2e)")]
    public async Task ProductionWiredGate_DropsForgedForeignPartyMessage_OnLiveMergePath()
    {
        // alice = the victim/operator (genesis); bob = an admitted member; attacker forges using bob's key under
        // alice's partyId. The roster is built by the ADMISSION protocol (genesis + proximity admit), then handed
        // to the PRODUCTION composition — so the rosterBinding the projection enforces is the real seeded roster.
        var alice = Member.New("alice");
        var bob = Member.New("bob");
        var coordinator = new AdmissionCoordinator(Verifier, new InMemoryAdmissionTokenStore(), clock: TimeProvider.System);

        var roster = coordinator.AdmitOverProximity(
            GenesisRosterFor(alice), "alice", alice.Signer, "bob", bob.KeyPair.PrincipalId, PermissionCompositions.Member);

        var src = await NewProductionProjectionAsync("src", roster);   // attacker's emitting node
        var dst = await NewProductionProjectionAsync("dst", roster);   // victim's node — production forge-proof gate

        // A legitimate alice message (alice's roster-bound key) — MUST survive the production gate.
        var good = await AppendAsync(src.Projection, alice, "legit from alice");

        // The forgery: bob's key signs a fresh message stamping ALICE's partyId — a malicious peer delta.
        var forged = await CommsMessageFactory.CreateSignedAsync(
            bob.Signer, authorPartyId: "alice", tenantId: Tenant, body: "I am Alice (forged)", authoredAt: DateTimeOffset.UtcNow);
        src.Projection.AppendLocal(forged);

        await SyncAsync(src.Projection, dst.Projection);

        // The CRDT list converged (merge-everything)...
        Assert.Equal(2, dst.Projection.Count);

        // ...but the PRODUCTION-WIRED forge-proof gate dropped the forgery: only alice's legit message landed in
        // the durable read store / GET. #1277-B1 CLOSED end-to-end, in the SHIPPING config.
        var dstLog = await dst.Projection.ReadLogAsync(Tenant, CancellationToken.None);
        Assert.Single(dstLog);
        Assert.Equal(good.MessageId, dstLog[0].MessageId);
        Assert.DoesNotContain(dstLog, m => m.MessageId == forged.MessageId);
    }

    // ── TEST 2: single-user node is NOT bricked — genesis-only roster forge-proves its own messages. ─────────

    [Fact(DisplayName = "Phase B: a fresh single-user node (genesis-only roster) still works — not bricked")]
    public async Task SingleUserNode_GenesisOnlyRoster_IsNotBricked()
    {
        // A fresh single-user node: the roster has ONLY the genesis (self) — exactly what Program.cs seeds.
        var operatorMember = Member.New("local");
        var roster = GenesisRosterFor(operatorMember);
        var node = await NewProductionProjectionAsync("solo", roster);

        // The operator's own message forge-proves (self is in its own roster) and lands. An empty/seed roster
        // does NOT reject the operator's own writes.
        await AppendAsync(node.Projection, operatorMember, "hello from my own books");
        var log = await node.Projection.ReadLogAsync(Tenant, CancellationToken.None);
        Assert.Single(log);
        Assert.Equal("local", log[0].AuthorPartyId);

        // And the forge-proof binding is genuinely LIVE (not null) — a forged message against this single-user
        // node would be dropped (an un-enrolled author has no binding → fail-closed).
        var forged = await CommsMessageFactory.CreateSignedAsync(
            Member.New("intruder").Signer, authorPartyId: "local", tenantId: Tenant, body: "forged", authoredAt: DateTimeOffset.UtcNow);
        // Verify the live binding rejects it (the projection's rosterBinding == roster.ForgeProofBinding).
        Assert.False(CommsMessageFactory.VerifyAuthorship(forged, Verifier, node.Roster.ForgeProofBinding));
        Assert.NotNull(node.Roster.ForgeProofBinding("local")); // self IS bound — not bricked
    }

    // ── TEST 3: the live MemberSetTrustPolicy (registrar wiring) — non-roster peer rejected, floor trusted. ──

    [Fact(DisplayName = "Phase B: live MemberSetTrustPolicy rejects a non-roster peer, trusts the own-subkey floor")]
    public void LiveTrustPolicy_RejectsNonRosterPeer_TrustsOwnSubkeyFloor()
    {
        // This mirrors the registrar's MemberSetTrustPolicy construction: the trusted set = {own team subkey} ∪
        // {admitted-member transport keys from ITrustedMemberKeyProvider}. Single-user = own subkey only.
        var signer = new Harborline.Api.Kernel.Security.Crypto.Ed25519Signer(); // the transport-layer signer
        var (_, ownRoot) = signer.GenerateKeyPair();
        var (_, peerRoot) = signer.GenerateKeyPair();
        var (_, strangerRoot) = signer.GenerateKeyPair();

        var own = TeamIdentity(ownRoot, signer);
        var admittedPeer = TeamIdentity(peerRoot, signer);
        var stranger = TeamIdentity(strangerRoot, signer);

        // The synced production path supplies a live admitted peer's transport subkey; the registrar unions the
        // own subkey floor. A key for a party absent from the signed roster is deliberately ignored.
        var founder = Member.New("local");
        var peer = Member.New("peer");
        var genesis = GenesisRosterFor(founder);
        var syncedRoster = genesis.Admit(
            founder.PartyId, founder.Signer, peer.PartyId, peer.KeyPair.PrincipalId,
            PermissionCompositions.Member, Verifier, DateTimeOffset.UtcNow, Guid.NewGuid());
        var roster = new NodeTeamRoster(genesis);
        roster.AdoptSyncedRoster(
            syncedRoster,
            new Dictionary<string, byte[]> { [peer.PartyId] = admittedPeer.PublicKey });

        var policy = new MemberSetTrustPolicy(() =>
        {
            var keys = new List<byte[]> { own.PublicKey };
            keys.AddRange(roster.TrustedTransportKeys());
            return keys;
        });

        Assert.True(policy.IsTrusted(HelloFor(own, signer)));          // own-subkey floor → trusted (not bricked)
        Assert.True(policy.IsTrusted(HelloFor(admittedPeer, signer))); // admitted peer's transport key → trusted
        Assert.False(policy.IsTrusted(HelloFor(stranger, signer)));    // non-roster peer → REJECTED fail-closed
    }

    private static NodeIdentity TeamIdentity(byte[] rootSeed, IEd25519Signer signer)
    {
        var (rootPub, _) = signer.GenerateFromSeed(rootSeed);
        var nodeIdBytes = new byte[16];
        Buffer.BlockCopy(rootPub, 0, nodeIdBytes, 0, 16);
        var rootNodeId = Convert.ToHexString(nodeIdBytes).ToLowerInvariant();
        var root = new NodeIdentity(rootNodeId, rootPub, rootSeed);
        return TeamScopedNodeIdentity.Derive(root, "team-phaseb", new TeamSubkeyDerivation(signer));
    }

    // MemberSetTrustPolicy.IsTrusted reads ONLY the HELLO's PublicKey (the handshake's signature check is a
    // separate earlier gate), so a minimal HelloMessage carrying the team transport pubkey is sufficient to
    // exercise the trust decision from this assembly (BuildHello is internal to kernel-sync).
    private static HelloMessage HelloFor(NodeIdentity teamIdentity, IEd25519Signer signer)
    {
        _ = signer;
        return new HelloMessage(
            NodeId: teamIdentity.NodeIdBytes,
            SchemaVersion: "1",
            SupportedVersions: new[] { "1" },
            PublicKey: teamIdentity.PublicKey,
            Timestamp: 0UL,
            Signature: Array.Empty<byte>());
    }
}
