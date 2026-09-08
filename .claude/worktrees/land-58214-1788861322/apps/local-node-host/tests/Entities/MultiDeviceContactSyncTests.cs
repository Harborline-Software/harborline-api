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
/// Multi-device INC-5 ACCEPTANCE — the milestone. Two <see cref="GossipDaemon"/>
/// instances over REAL loopback TCP (<see cref="TcpSyncDaemonTransport"/>), each
/// fed by its own node-host <see cref="ContactCrdtProjection"/> over its own
/// SQLite store, discover + connect + authenticate LIVE (same-root TRUSTED via
/// the daemon's own initiator + accept-loop trust gate), and a contact CREATE,
/// UPDATE, and DELETE on instance A's store appears in instance B's EF READ
/// store through the live trust + transport + CRDT path. This is the automated
/// "edit on A → see on B" proof.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the end-to-end proof the daemon goes live.</b> Unlike
/// <c>ContactCrdtConvergenceTests</c> (which exchanged deltas in-proc with NO
/// networking) and <c>kernel-sync MultiDeviceConnectivityTests</c> (which used a
/// hand-rolled test responder and a Noop delta path), this test runs the
/// PRODUCTION daemon code on both sides: A's daemon initiates over TCP with the
/// LIVE trust gate; B's daemon accept loop authenticates the initiator with the
/// LIVE trust gate and applies the inbound contacts delta through B's real
/// <see cref="ContactCrdtProjection"/>, whose value-aware Changed trigger (the
/// earlier repository ticket #1260 F1 fix) reconciles UPDATEs and DELETEs into B's EF read store.
/// </para>
/// <para>
/// <b>Container-bridge scope (INC-5 pre-condition #3 — FLAGGED follow-up).</b>
/// In production the gossip daemon lives in the per-team CHILD container while
/// the <see cref="ContactCrdtProjection"/> lives in the node-host OUTER
/// container; the composition-root rewire that auto-wires the projection into
/// the child-container daemon is deferred (it changes the kernel-runtime
/// <c>TeamServiceRegistrar</c> substrate). This test composes the daemon
/// DIRECTLY with the real projection — the EXACT object graph that rewire will
/// produce — so the live data-flow path is proven now and the deferral is
/// purely the production auto-wiring, not the mechanism.
/// </para>
/// <para>
/// <b>A real two-physical-machine run is a separate manual acceptance</b>
/// (route to po-mac / po-win / CIC): bind the listener to a LAN-routable
/// address (0.0.0.0:7473) on two hosts on the same subnet, advertise via mDNS,
/// and confirm the same create/update/delete convergence over the wire.
/// </para>
/// </remarks>
public sealed class MultiDeviceContactSyncTests : IAsyncLifetime
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

    [Fact(DisplayName = "INC-5: edit on A → appears on B over LIVE daemon (TCP + trust gate + CRDT) for create/update/delete")]
    public async Task Edit_On_A_Appears_On_B_Over_Live_Daemon_For_Create_Update_Delete()
    {
        var signer = new Ed25519Signer();
        var (_, sharedRoot) = signer.GenerateKeyPair();

        // Two devices, ONE shared root seed → the SAME team key (the trust
        // anchor) but DISTINCT install node ids — exactly the single-user-multi-
        // device topology the live trust gate authenticates.
        var deviceA = TeamIdentity(sharedRoot, "team-home", signer, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var deviceB = TeamIdentity((byte[])sharedRoot.Clone(), "team-home", signer, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        Assert.Equal(deviceA.PublicKey, deviceB.PublicKey);

        // Each replica: its own CRDT engine + SQLite store + ContactCrdtProjection.
        var a = await NewReplicaAsync("A");
        var b = await NewReplicaAsync("B");

        // B listens on a real loopback TCP port; B's daemon accept loop runs the
        // LIVE shared-root trust gate and feeds inbound deltas to B's projection.
        var transportB = new TcpSyncDaemonTransport("tcp://127.0.0.1:0");
        _cleanup.Add(transportB);
        var endpointB = transportB.ListenEndpoint!;
        var daemonB = BuildDaemon(transportB, deviceB, signer, b.Projection, new SharedRootTrustPolicy(deviceB.PublicKey));
        _cleanup.Add(daemonB);
        await daemonB.StartListeningAsync(CancellationToken.None);

        // A: outbound TCP transport; A's daemon initiates with the LIVE trust
        // gate and ships A's contacts delta from A's projection each round.
        var transportA = new TcpSyncDaemonTransport();
        _cleanup.Add(transportA);
        var daemonA = BuildDaemon(transportA, deviceA, signer, a.Projection, new SharedRootTrustPolicy(deviceA.PublicKey));
        _cleanup.Add(daemonA);
        daemonA.AddPeer(endpointB, deviceB.PublicKey);

        // Observe B's reconcile completion via its projection so the assertions
        // can wait for the LIVE inbound-delta → reconcile to land in B's EF store
        // without racing the 1-second gossip tick.
        await daemonA.StartAsync(CancellationToken.None);

        // ── CREATE on A → appears in B's READ store ──────────────────────────
        var party = await CreateContactAsync(a, "Ada Lovelace");
        await WaitForEfAsync(b, party.Id, expected: "Ada Lovelace",
            because: "a contact CREATEd on A must reach B's EF read store over the live daemon.");

        // ── UPDATE on A → the rename appears in B's READ store (F1 value path) ─
        var renamed = await UpdateContactAsync(a, party, "Ada, Countess of Lovelace");
        await WaitForEfAsync(b, party.Id, expected: "Ada, Countess of Lovelace",
            because: "an UPDATE to an already-synced contact must reach B's EF store via the live value-aware trigger.");
        Assert.Equal("Ada, Countess of Lovelace", renamed.DisplayName);

        // ── DELETE on A (tombstone) → the contact is hidden in B's READ store ─
        await DeleteContactAsync(a, party);
        await WaitForEfGoneAsync(b, party.Id,
            because: "a tombstone DELETE on A must hide the contact from B's EF read store via the live trigger.");

        await daemonA.StopAsync(CancellationToken.None);
        await daemonB.StopListeningAsync(CancellationToken.None);
    }

    [Fact(DisplayName = "INC-5: a DIFFERENT-ROOT peer is REJECTED on the live path — no contact crosses (fail-closed)")]
    public async Task DifferentRoot_Peer_Is_Rejected_No_Contact_Crosses()
    {
        var signer = new Ed25519Signer();
        var (_, rootA) = signer.GenerateKeyPair();
        var (_, rootB) = signer.GenerateKeyPair(); // DIFFERENT root — a stranger on the LAN
        var deviceA = TeamIdentity(rootA, "team-home", signer, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var deviceB = TeamIdentity(rootB, "team-home", signer, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        Assert.NotEqual(deviceA.PublicKey, deviceB.PublicKey);

        var a = await NewReplicaAsync("A");
        var b = await NewReplicaAsync("B");

        var transportB = new TcpSyncDaemonTransport("tcp://127.0.0.1:0");
        _cleanup.Add(transportB);
        var endpointB = transportB.ListenEndpoint!;
        // B trusts only ITS OWN root — A (different root) is a stranger.
        var daemonB = BuildDaemon(transportB, deviceB, signer, b.Projection, new SharedRootTrustPolicy(deviceB.PublicKey));
        _cleanup.Add(daemonB);
        var bRejected = new SemaphoreSlim(0, 8);
        daemonB.FrameReceived += (_, e) => { if (e.FrameType == GossipFrameType.HandshakeFailure) bRejected.Release(); };
        await daemonB.StartListeningAsync(CancellationToken.None);

        var transportA = new TcpSyncDaemonTransport();
        _cleanup.Add(transportA);
        var daemonA = BuildDaemon(transportA, deviceA, signer, a.Projection, new SharedRootTrustPolicy(deviceA.PublicKey));
        _cleanup.Add(daemonA);
        daemonA.AddPeer(endpointB, deviceB.PublicKey);

        var party = await CreateContactAsync(a, "Secret Contact");
        await daemonA.StartAsync(CancellationToken.None);

        // B's accept loop rejects the stranger (fail-closed).
        Assert.True(await bRejected.WaitAsync(TimeSpan.FromSeconds(15)),
            "B's live accept loop must reject a different-root initiator (PeerUntrusted).");

        // Give several gossip rounds a chance to (wrongly) leak the contact, then
        // prove B's READ store NEVER received it.
        await Task.Delay(2000);
        await using (var ctx = await b.Factory.CreateDbContextAsync())
        {
            var leaked = await ctx.Set<Party>().IgnoreQueryFilters().AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == party.Id);
            Assert.Null(leaked);
        }

        await daemonA.StopAsync(CancellationToken.None);
        await daemonB.StopListeningAsync(CancellationToken.None);
    }

    [Fact(DisplayName = "Push-on-change: a local edit converges on B via TriggerPushAsync WITHOUT waiting a periodic round")]
    public async Task Local_Edit_Pushes_Immediately_Via_TriggerPush_No_Periodic_Round()
    {
        var signer = new Ed25519Signer();
        var (_, sharedRoot) = signer.GenerateKeyPair();
        var deviceA = TeamIdentity(sharedRoot, "team-home", signer, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var deviceB = TeamIdentity((byte[])sharedRoot.Clone(), "team-home", signer, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");

        var a = await NewReplicaAsync("A");
        var b = await NewReplicaAsync("B");

        // B listens + authenticates over real loopback TCP, feeding inbound deltas to B's projection.
        var transportB = new TcpSyncDaemonTransport("tcp://127.0.0.1:0");
        _cleanup.Add(transportB);
        var endpointB = transportB.ListenEndpoint!;
        var daemonB = BuildDaemon(transportB, deviceB, signer, b.Projection, new SharedRootTrustPolicy(deviceB.PublicKey));
        _cleanup.Add(daemonB);
        await daemonB.StartListeningAsync(CancellationToken.None);

        // A's daemon has a DELIBERATELY LONG periodic interval (1h) — so a round inside the
        // test window can ONLY be the explicit push, never the timer. And we do NOT call
        // daemonA.StartAsync, so the periodic loop is not even running.
        var transportA = new TcpSyncDaemonTransport();
        _cleanup.Add(transportA);
        var daemonA = BuildDaemon(transportA, deviceA, signer, a.Projection, new SharedRootTrustPolicy(deviceA.PublicKey),
            roundIntervalSeconds: 3600);
        _cleanup.Add(daemonA);
        daemonA.AddPeer(endpointB, deviceB.PublicKey);

        // The PRODUCTION wiring (LocalNodeWorker.WirePushOnChange): a local op on A's
        // projection fires an immediate authenticated push.
        a.Projection.LocalDeltaProduced += (_, _) =>
            _ = Task.Run(() => daemonA.TriggerPushAsync(
                OutboundSyncLane.Foreground,
                CancellationToken.None));

        // A local CREATE on A — ProjectUpsert raises LocalDeltaProduced → TriggerPushAsync.
        var party = await CreateContactAsync(a, "Grace Hopper");

        // It converges on B with NO periodic round running — push-on-change alone carried it.
        await WaitForEfAsync(b, party.Id, expected: "Grace Hopper",
            because: "push-on-change must carry a local edit to B WITHOUT the periodic anti-entropy round.");

        await daemonB.StopListeningAsync(CancellationToken.None);
    }

    // ───────────────────────────── helpers ─────────────────────────────

    private GossipDaemon BuildDaemon(
        ISyncDaemonTransport transport,
        NodeIdentity identity,
        IEd25519Signer signer,
        ContactCrdtProjection projection,
        IPeerTrustPolicy trust,
        int roundIntervalSeconds = 1) =>
        new(
            transport,
            new VectorClock(),
            Options.Create(new GossipDaemonOptions
            {
                RoundIntervalSeconds = roundIntervalSeconds,
                PeerPickCount = 1,
                ConnectTimeoutSeconds = 5,
                DeadPeerBackoffSeconds = 2,
            }),
            new InMemoryNodeIdentityProvider(identity),
            signer,
            // The real contacts projection is BOTH the outbound producer and the
            // inbound sink — the exact object graph the deferred container-bridge
            // will produce in the per-team child container.
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
        var dir = Path.Combine(Path.GetTempPath(), $"harborline-inc5-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        var connectionString = $"Data Source={Path.Combine(dir, "local-node.db")};Pooling=False";

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHarborlineEntityModule, PeopleEntityModule>();
        services.AddDbContextFactory<LocalNodeDbContext>(opt => opt.UseSqlite(connectionString));
        services.AddSingleton<ICrdtEngine, YDotNetCrdtEngine>(); // real backend — convergence must be honest

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

    private static async Task<Party> UpdateContactAsync(Replica r, Party party, string newDisplayName)
    {
        Party stamped;
        await using (var ctx = await r.Factory.CreateDbContextAsync())
        {
            var existing = await ctx.Set<Party>().IgnoreQueryFilters().FirstAsync(p => p.Id == party.Id);
            stamped = existing with
            {
                DisplayName = newDisplayName, UpdatedAt = new Instant(System.TimeProvider.System.GetUtcNow()), UpdatedBy = Actor,
                Version = existing.Version + 1,
            };
            ctx.Entry(existing).State = EntityState.Detached;
            ctx.Entry(stamped).State = EntityState.Modified;
            await ctx.SaveChangesAsync();
        }
        r.Projection.ProjectUpsert(stamped);
        return stamped;
    }

    private static async Task DeleteContactAsync(Replica r, Party party)
    {
        Party tombstoned;
        await using (var ctx = await r.Factory.CreateDbContextAsync())
        {
            var existing = await ctx.Set<Party>().IgnoreQueryFilters().FirstAsync(p => p.Id == party.Id);
            var now = new Instant(System.TimeProvider.System.GetUtcNow());
            tombstoned = existing with
            {
                DeletedAt = now, DeletedBy = Actor, UpdatedAt = now, UpdatedBy = Actor,
                Version = existing.Version + 1,
            };
            ctx.Entry(existing).State = EntityState.Detached;
            ctx.Entry(tombstoned).State = EntityState.Modified;
            await ctx.SaveChangesAsync();
        }
        r.Projection.ProjectDelete(tombstoned);
    }

    private static async Task<string?> ReadDisplayNameAsync(Replica r, PartyId id)
    {
        await using var ctx = await r.Factory.CreateDbContextAsync();
        var p = await ctx.Set<Party>().IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id);
        return p?.DeletedAt is null ? p?.DisplayName : null;
    }

    /// <summary>Poll B's READ store until it reflects <paramref name="expected"/> or the deadline elapses.</summary>
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

    /// <summary>Poll B's READ store until the contact is gone (tombstone converged) or the deadline elapses.</summary>
    private static async Task WaitForEfGoneAsync(Replica r, PartyId id, string because)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (await ReadDisplayNameAsync(r, id) is null) return;
            await Task.Delay(150);
        }
        Assert.Fail($"{because} The contact is still visible in B's EF store after the deadline.");
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
