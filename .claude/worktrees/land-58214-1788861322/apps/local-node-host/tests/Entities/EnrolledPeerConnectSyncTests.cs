using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Blocks.People.Foundation.Data;
using Harborline.Api.Foundation.Assets.Common;
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

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// gap C — the PEER-CONNECTION half proven END-TO-END with the ENROLLMENT trust
/// gate (the REAL two-user case), not the shared-root shortcut.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="MultiDeviceContactSyncTests"/> already proves "edit on A → see on
/// B over the live daemon" for two SAME-ROOT devices (one shared seed → the same
/// team key, trusted via <see cref="SharedRootTrustPolicy"/>). That is the
/// single-user-multi-device topology. It does NOT exercise the case gap-A
/// (#1296-F2) made real: two DISTINCT-root users (alice + bob, distinct principal
/// + transport keys) who trust each other ONLY because each is an enrolled member
/// of the same roster — the <see cref="MemberSetTrustPolicy"/> production gate.
/// </para>
/// <para>
/// <b>What this adds.</b> This is the address/connection half (gap C) standing on
/// the trust half (gap-A, done): two distinct-root nodes, each with its OWN
/// CRDT engine + SQLite store + projection, where one is configured as the
/// other's STATIC PEER (the Harborline App injects this address as
/// <c>LocalNode__Sync__Peers</c>; here we drive the daemon's <c>AddPeer</c>
/// directly — the EXACT call <c>LocalNodeWorker.WireCrossMachineSyncAsync</c>
/// makes from that config). The trust gate is <see cref="MemberSetTrustPolicy"/>
/// seeded with the OTHER node's TRANSPORT public key — exactly what
/// <c>NodeTeamRoster.AdmitPeer</c> records on redeem. A contact CREATEd on A
/// reaches B's read store: the two enrolled, distinct-root nodes REACH each other
/// and sync, end-to-end, with the production trust gate.
/// </para>
/// <para>
/// <b>Why this is the right gap-C proof.</b> The address (static peer) is the
/// thing gap C wires; the roster-validated transport key is the thing gap-A
/// wired. This test stands them up together so the connection half is proven on
/// the enrollment trust gate, not only the same-root floor — closing the
/// "shared-root shortcut CANNOT deliver real two-user sync" finding (cerebrum
/// [2026-06-20]).
/// </para>
/// </remarks>
public sealed class EnrolledPeerConnectSyncTests : IAsyncLifetime
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

    [Fact(DisplayName = "gap C: two DISTINCT-root ENROLLED nodes connect via static peer + MemberSetTrustPolicy and sync A→B")]
    public async Task DistinctRoot_Enrolled_Nodes_Connect_Via_Static_Peer_And_Sync()
    {
        var signer = new Ed25519Signer();
        var (_, rootA) = signer.GenerateKeyPair();
        var (_, rootB) = signer.GenerateKeyPair(); // DISTINCT root — alice and bob, NOT one shared seed.

        // Both join the SAME team id (the roster they were both admitted into). Each derives its OWN team-scoped
        // TRANSPORT subkey (HKDF(own-root, teamId), ADR 0032) — DISTINCT keys, because the roots differ. This is
        // the exact property the shared-root shortcut could never deliver.
        var deviceA = TeamIdentity(rootA, "team-office", signer, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var deviceB = TeamIdentity(rootB, "team-office", signer, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        Assert.NotEqual(deviceA.PublicKey, deviceB.PublicKey); // distinct-root ⇒ distinct transport keys.

        var a = await NewReplicaAsync("A");
        var b = await NewReplicaAsync("B");

        // The ENROLLED trust sets — what each node's roster holds AFTER enrollment (gap-A): each member's own
        // team-scoped transport subkey PLUS the other admitted member's. MemberSetTrustPolicy reads this snapshot
        // and trusts the peer's wire HELLO key iff it is in the set. This is the production gate
        // (DefaultTeamServiceRegistrar registers exactly this), NOT the shared-root floor.
        var rosterTrustKeys = new List<byte[]> { deviceA.PublicKey, deviceB.PublicKey };

        // B LISTENS on a real loopback TCP port; its accept loop runs the MEMBER-SET trust gate and feeds inbound
        // deltas to B's projection. (The Harborline App injects 0.0.0.0:7473 as the LAN bind; the test uses loopback so the
        // accept/dial path is real TCP without needing a routable interface.)
        var transportB = new TcpSyncDaemonTransport("tcp://127.0.0.1:0");
        _cleanup.Add(transportB);
        var endpointB = transportB.ListenEndpoint!;
        var daemonB = BuildDaemon(transportB, deviceB, signer, b.Projection, new MemberSetTrustPolicy(rosterTrustKeys));
        _cleanup.Add(daemonB);
        await daemonB.StartListeningAsync(CancellationToken.None);

        // A: outbound TCP transport; A's daemon initiates with the MEMBER-SET trust gate. The STATIC-PEER dial is
        // the gap-C wiring: AddPeer(B's endpoint, B's transport key) — the EXACT call
        // LocalNodeWorker.WireCrossMachineSyncAsync makes from LocalNode:Sync:Peers (the address the Harborline App injects).
        var transportA = new TcpSyncDaemonTransport();
        _cleanup.Add(transportA);
        var daemonA = BuildDaemon(transportA, deviceA, signer, a.Projection, new MemberSetTrustPolicy(rosterTrustKeys));
        _cleanup.Add(daemonA);
        daemonA.AddPeer(endpointB, deviceB.PublicKey);

        await daemonA.StartAsync(CancellationToken.None);

        // CREATE on A → appears in B's READ store: the two DISTINCT-root, ENROLLED nodes reached each other and
        // synced over the wire, gated by the production MemberSetTrustPolicy.
        var party = await CreateContactAsync(a, "Ada Lovelace");
        await WaitForEfAsync(b, party.Id, expected: "Ada Lovelace",
            because: "a contact CREATEd on A must reach B's read store over the live daemon — two distinct-root "
                + "ENROLLED nodes connecting via a static peer + MemberSetTrustPolicy (the gap-C address half on "
                + "the gap-A trust half).");

        await daemonA.StopAsync(CancellationToken.None);
        await daemonB.StopListeningAsync(CancellationToken.None);
    }

    [Fact(DisplayName = "gap C: a NON-ROSTER peer (correct address, wrong key) is REJECTED — address alone is not trust")]
    public async Task NonRoster_Peer_With_Correct_Address_Is_Rejected_No_Contact_Crosses()
    {
        var signer = new Ed25519Signer();
        var (_, rootA) = signer.GenerateKeyPair();
        var (_, rootB) = signer.GenerateKeyPair();
        var (_, rootCharlie) = signer.GenerateKeyPair(); // an UN-enrolled stranger who knows B's address.

        var deviceA = TeamIdentity(rootA, "team-office", signer, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var deviceB = TeamIdentity(rootB, "team-office", signer, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        var deviceCharlie = TeamIdentity(rootCharlie, "team-office", signer, "cccccccccccccccccccccccccccccccc");

        var b = await NewReplicaAsync("B");
        var charlie = await NewReplicaAsync("Charlie");

        // B's roster trusts ONLY alice + bob — charlie is NOT a member, even though he knows B's address.
        var rosterTrustKeys = new List<byte[]> { deviceA.PublicKey, deviceB.PublicKey };

        var transportB = new TcpSyncDaemonTransport("tcp://127.0.0.1:0");
        _cleanup.Add(transportB);
        var endpointB = transportB.ListenEndpoint!;
        var daemonB = BuildDaemon(transportB, deviceB, signer, b.Projection, new MemberSetTrustPolicy(rosterTrustKeys));
        _cleanup.Add(daemonB);
        var bRejected = new SemaphoreSlim(0, 8);
        daemonB.FrameReceived += (_, e) => { if (e.FrameType == GossipFrameType.HandshakeFailure) bRejected.Release(); };
        await daemonB.StartListeningAsync(CancellationToken.None);

        // Charlie has the CORRECT address (gap-C static-peer config could carry any address) but his transport key
        // is NOT in B's roster — so B's MemberSetTrustPolicy rejects him fail-closed. Address is reachability, NOT
        // trust: this is the security property the Harborline App relies on when it injects an address-only static peer.
        var transportCharlie = new TcpSyncDaemonTransport();
        _cleanup.Add(transportCharlie);
        var daemonCharlie = BuildDaemon(transportCharlie, deviceCharlie, signer, charlie.Projection,
            new MemberSetTrustPolicy(new List<byte[]> { deviceCharlie.PublicKey })); // charlie trusts himself; irrelevant — B gates.
        _cleanup.Add(daemonCharlie);
        daemonCharlie.AddPeer(endpointB, deviceB.PublicKey);

        var party = await CreateContactAsync(charlie, "Exfiltration Attempt");
        await daemonCharlie.StartAsync(CancellationToken.None);

        Assert.True(await bRejected.WaitAsync(TimeSpan.FromSeconds(15)),
            "B's accept loop must reject a non-roster initiator even when the address is correct (PeerUntrusted).");

        // Give rounds a chance to (wrongly) leak, then prove B's read store NEVER received charlie's contact.
        await Task.Delay(2000);
        await using (var ctx = await b.Factory.CreateDbContextAsync())
        {
            var leaked = await ctx.Set<Party>().IgnoreQueryFilters().AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == party.Id);
            Assert.Null(leaked);
        }

        await daemonCharlie.StopAsync(CancellationToken.None);
        await daemonB.StopListeningAsync(CancellationToken.None);
    }

    // ───────────────────────────── helpers ─────────────────────────────

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

    private static NodeIdentity TeamIdentity(byte[] rootSeed, string teamId, IEd25519Signer signer, string installNodeId)
    {
        var (rootPub, _) = signer.GenerateFromSeed(rootSeed);
        var root = new NodeIdentity(installNodeId, rootPub, rootSeed);
        return TeamScopedNodeIdentity.Derive(root, teamId, new TeamSubkeyDerivation(signer));
    }

    private async Task<Replica> NewReplicaAsync(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"harborline-gapc-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        var connectionString = $"Data Source={Path.Combine(dir, "local-node.db")};Pooling=False";

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHarborlineEntityModule, PeopleEntityModule>();
        services.AddDbContextFactory<LocalNodeDbContext>(opt => opt.UseSqlite(connectionString));
        services.AddSingleton<ICrdtEngine, YDotNetCrdtEngine>(); // real backend — convergence must be honest.

        var sp = services.BuildServiceProvider();
        var factory = sp.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
        await using (var ctx = await factory.CreateDbContextAsync())
            await ctx.Database.EnsureCreatedAsync();

        var projection = new ContactCrdtProjection(
            sp.GetRequiredService<ICrdtEngine>(), factory, NullLogger<ContactCrdtProjection>.Instance);

        var replica = new Replica { Name = name, Dir = dir, Sp = sp, Factory = factory, Projection = projection };
        _cleanup.Add(replica);
        return replica;
    }

    private static async Task<Party> CreateContactAsync(Replica r, string displayName)
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

    private static async Task<string?> ReadDisplayNameAsync(Replica r, PartyId id)
    {
        await using var ctx = await r.Factory.CreateDbContextAsync();
        var p = await ctx.Set<Party>().IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id);
        return p?.DeletedAt is null ? p?.DisplayName : null;
    }

    private static async Task WaitForEfAsync(Replica r, PartyId id, string expected, string because)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        string? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = await ReadDisplayNameAsync(r, id);
            if (last == expected) return;
            await Task.Delay(150);
        }
        Assert.Fail($"{because} Last observed value in B's EF store: '{last ?? "(absent)"}', expected '{expected}'.");
    }

    private sealed class Replica : IAsyncDisposable
    {
        public required string Name { get; init; }
        public required string Dir { get; init; }
        public required ServiceProvider Sp { get; init; }
        public required IDbContextFactory<LocalNodeDbContext> Factory { get; init; }
        public required ContactCrdtProjection Projection { get; init; }

        public async ValueTask DisposeAsync()
        {
            await Projection.DisposeAsync();
            await Sp.DisposeAsync();
        }
    }
}
