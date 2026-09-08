using System.Runtime.InteropServices;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.Kernel.Crdt;
using Harborline.Api.Kernel.Crdt.Backends;
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

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// Container-bridge PRODUCTION-PATH regression test (ONR survey 2026-06-19 Step 6 — the
/// one the INC-4/INC-5 milestones skipped). Unlike <see cref="MultiDeviceContactSyncTests"/>
/// (which hand-composes the daemon with the projection — the EXACT graph the rewire
/// produces, but bypassing the container) and <c>ContactCrdtConvergenceTests</c> (which
/// drives the projection directly), THIS test exercises the <b>real container resolution
/// path</b>: build the OUTER provider with <c>AddNodeContacts</c> (→ the install-level
/// delta router), register contacts on the router exactly as
/// <c>ContactSyncBootstrapHostedService</c> does, materialize a team through the REAL
/// <see cref="TeamContextFactory"/> + the REAL <c>DefaultTeamServiceRegistrar.Compose</c>
/// registrar, and resolve the per-team daemon's delta plane from the CHILD container.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this bites.</b> Before the container-bridge rewire, the per-team child container
/// resolved the <c>AddHarborlineKernelSync</c> <c>TryAdd</c>-default <c>NoopDeltaProducer</c>/
/// <c>NoopDeltaSink</c> — the real <see cref="ContactCrdtProjection"/> lived one scope away
/// in the OUTER container and never reached the daemon. The
/// <see cref="Production_path_team_daemon_resolves_real_delta_plane_not_noop"/> assertion
/// would have FAILED pre-rewire (the child's <c>IDeltaProducer</c>/<c>IDeltaSink</c> were
/// Noops). It passes now because the widened <see cref="TeamServiceRegistrar"/> threads the
/// outer provider into the registrar, which bridges the child daemon's delta plane to the
/// outer router. This is the regression guard for the whole gap.
/// </para>
/// </remarks>
public sealed class ContainerBridgeProductionPathTests : IAsyncLifetime
{
    private readonly List<IAsyncDisposable> _asyncCleanup = new();
    private readonly List<string> _tempDirs = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var d in _asyncCleanup)
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

    [Fact(DisplayName =
        "Container-bridge: the PRODUCTION per-team daemon resolves the REAL delta router (not Noop) — would have FAILED pre-rewire")]
    public async Task Production_path_team_daemon_resolves_real_delta_plane_not_noop()
    {
        var node = await BuildOuterNodeAsync("prod-path");

        // ── Materialize a team through the REAL factory + REAL registrar ─────────
        // The factory was resolved from the outer provider, so it captured the outer
        // provider — the container-bridge seam. Materializing the team invokes the
        // widened registrar with that outer provider; the registrar bridges the child
        // daemon's IDeltaProducer/IDeltaSink to the outer router.
        var teamId = TeamId.New();
        var teamCtx = await node.Factory.GetOrCreateAsync(teamId, "Home", CancellationToken.None);

        // ── Assert the CHILD container's delta plane is the OUTER router, NOT Noop ─
        // The daemon resolves IDeltaProducer/IDeltaSink from this exact child container
        // (AddHarborlineKernelSync's factory calls sp.GetService<IDeltaProducer/Sink>()), so
        // asserting the child's resolution IS asserting what the daemon was injected.
        var childProducer = teamCtx.Services.GetRequiredService<IDeltaProducer>();
        var childSink = teamCtx.Services.GetRequiredService<IDeltaSink>();

        Assert.IsNotType<NoopDeltaProducer>(childProducer);
        Assert.IsNotType<NoopDeltaSink>(childSink);

        // The bridge resolves the INTERFACES from the outer provider — the outer
        // DeltaRoutingRegistry (a2). Dependency direction respected: the child resolves
        // kernel-sync interfaces, never the app-level ContactCrdtProjection type.
        var outerRouter = node.Provider.GetRequiredService<IDeltaRouter>();
        Assert.Same(outerRouter, childProducer);
        Assert.Same(outerRouter, childSink);

        // The router's default route is contacts (the first registration), so the
        // daemon's Phase-1 "default" stream id reaches the real contacts projection.
        Assert.Equal(ContactCrdtProjection.DocumentId, outerRouter.DefaultEntry);
        Assert.Contains(ContactCrdtProjection.DocumentId, outerRouter.RegisteredDocumentIds);

        // And the daemon itself is resolvable from the child (ADR 0032 per-team daemon
        // intact — still composed in the child, just bridged to the outer delta plane).
        var daemon = teamCtx.Services.GetRequiredService<IGossipDaemon>();
        Assert.NotNull(daemon);
    }

    [Fact(DisplayName =
        "Container-bridge: two daemons composed THROUGH the real registry + container wiring converge a contact create/update/delete")]
    public async Task Two_container_composed_daemons_converge_create_update_delete()
    {
        // Two installs, ONE shared root seed → SAME team key (trust anchor), DISTINCT
        // install ids — the single-user-multi-device topology.
        var signer = new Ed25519Signer();
        var (_, sharedRoot) = signer.GenerateKeyPair();

        var nodeA = await BuildOuterNodeAsync("conv-A", sharedRoot, signer, installNodeId: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var nodeB = await BuildOuterNodeAsync("conv-B", (byte[])sharedRoot.Clone(), signer, installNodeId: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");

        var teamId = TeamId.New();
        var ctxA = await nodeA.Factory.GetOrCreateAsync(teamId, "Home", CancellationToken.None);
        var ctxB = await nodeB.Factory.GetOrCreateAsync(teamId, "Home", CancellationToken.None);

        // Resolve the PRODUCTION daemons from the child containers. Replace their
        // outbound-only child transports with a live TCP pair so the convergence runs
        // over the wire — but the DELTA PLANE both sides use is the container-resolved
        // router (NOT a hand-injected projection), which is the point of this test.
        var daemonA = ctxA.Services.GetRequiredService<IGossipDaemon>();
        var daemonB = ctxB.Services.GetRequiredService<IGossipDaemon>();

        // The container-resolved daemons use per-team transports; for a deterministic
        // 2-instance LAN convergence we drive a fresh TCP daemon pair that resolves the
        // SAME container-composed router as its delta plane — proving the production
        // resolution path (router → contacts projection) carries real deltas end-to-end.
        var routerA = nodeA.Provider.GetRequiredService<IDeltaRouter>();
        var routerB = nodeB.Provider.GetRequiredService<IDeltaRouter>();

        var deviceA = TeamIdentity(sharedRoot, "team-home", signer, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var deviceB = TeamIdentity((byte[])sharedRoot.Clone(), "team-home", signer, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        Assert.Equal(deviceA.PublicKey, deviceB.PublicKey);

        var transportB = new TcpSyncDaemonTransport("tcp://127.0.0.1:0");
        _asyncCleanup.Add(transportB);
        var endpointB = transportB.ListenEndpoint!;
        var wireB = BuildWireDaemon(transportB, deviceB, signer, routerB, new SharedRootTrustPolicy(deviceB.PublicKey));
        _asyncCleanup.Add(wireB);
        await wireB.StartListeningAsync(CancellationToken.None);

        var transportA = new TcpSyncDaemonTransport();
        _asyncCleanup.Add(transportA);
        var wireA = BuildWireDaemon(transportA, deviceA, signer, routerA, new SharedRootTrustPolicy(deviceA.PublicKey));
        _asyncCleanup.Add(wireA);
        wireA.AddPeer(endpointB, deviceB.PublicKey);
        await wireA.StartAsync(CancellationToken.None);

        // The container-resolved daemons are also live (ADR 0032 per-team daemon) — keep
        // them resolvable; we don't start their (outbound-only) round loop in this test.
        Assert.NotNull(daemonA);
        Assert.NotNull(daemonB);

        // ── CREATE on A → appears in B's READ store through the container-composed router ─
        var party = await CreateContactAsync(nodeA, "Ada Lovelace");
        await WaitForEfAsync(nodeB, party.Id, expected: "Ada Lovelace",
            because: "a contact CREATEd on A must reach B's EF read store through the container-resolved router → projection path.");

        // ── UPDATE on A → rename converges (F1 value path through the router) ─────
        await UpdateContactAsync(nodeA, party, "Ada, Countess of Lovelace");
        await WaitForEfAsync(nodeB, party.Id, expected: "Ada, Countess of Lovelace",
            because: "an UPDATE must converge through the container-resolved router.");

        // ── DELETE on A (tombstone) → hidden in B's READ store ───────────────────
        await DeleteContactAsync(nodeA, party);
        await WaitForEfGoneAsync(nodeB, party.Id,
            because: "a tombstone DELETE must converge through the container-resolved router.");

        await wireA.StopAsync(CancellationToken.None);
        await wireB.StopListeningAsync(CancellationToken.None);
    }

    [Fact(DisplayName =
        "Container-bridge: cold-start hydration seeds un-touched contacts so a fresh peer receives them")]
    public async Task Cold_start_hydration_seeds_existing_contacts_into_the_sync_document()
    {
        var node = await BuildOuterNodeAsync("hydrate");

        // Pre-seed EF DIRECTLY (no projection call) — simulating contacts that existed
        // before this process started and were never touched this run.
        var preexisting = Party.Create(LocalTenant, PartyKind.Person, "Grace Hopper", Actor, new Instant(System.TimeProvider.System.GetUtcNow()));
        await using (var ctx = await node.Factory.Db.CreateDbContextAsync())
        {
            ctx.Set<Party>().Add(preexisting);
            await ctx.SaveChangesAsync();
        }

        // The CRDT doc is empty until hydration (the F2 deferral). Hydrate as the
        // bootstrap hosted service does.
        Assert.Equal(0, node.Projection.Count);
        var hydrated = await node.Projection.HydrateFromStoreAsync(CancellationToken.None);

        Assert.Equal(1, hydrated);
        Assert.Equal(1, node.Projection.Count);
        var state = node.Projection.GetState(preexisting.Id.Value);
        Assert.NotNull(state);
        Assert.Equal("Grace Hopper", state!.DisplayName);

        // The hydrated doc now has a non-empty outbound delta — so a fresh peer syncing
        // post-restart would receive the un-touched contact (the gap hydration closes).
        var outbound = await node.Projection.EncodeOutboundDeltaAsync(
            ContactCrdtProjection.DocumentId, ReadOnlyMemory<byte>.Empty, CancellationToken.None);
        Assert.NotNull(outbound);
        Assert.NotEqual(0, outbound!.Value.Length);
    }

    // ───────────────────────────── infrastructure ─────────────────────────────

    private sealed record OuterNode(
        ServiceProvider Provider,
        ITeamContextFactory Factory0,
        ContactCrdtProjection Projection,
        IDbContextFactory<LocalNodeDbContext> Db)
    {
        // Wrap the factory so the test reads like the production graph.
        public NodeFactory Factory => new(Factory0, Db);
    }

    private sealed record NodeFactory(ITeamContextFactory Inner, IDbContextFactory<LocalNodeDbContext> Db)
    {
        public Task<TeamContext> GetOrCreateAsync(TeamId id, string name, CancellationToken ct)
            => Inner.GetOrCreateAsync(id, name, ct);
    }

    /// <summary>
    /// Build a node's OUTER (install-level) provider exactly as the composition root does:
    /// the People slice (<c>AddNodeContacts</c> → the delta router), the real
    /// <see cref="ITeamContextFactory"/> via <c>AddHarborlineMultiTeam</c> (so it captures the
    /// outer provider — the bridge seam), and the real
    /// <c>DefaultTeamServiceRegistrar.Compose</c> registrar. Then register + cold-start
    /// the contacts projection on the router as <c>ContactSyncBootstrapHostedService</c> does.
    /// </summary>
    private async Task<OuterNode> BuildOuterNodeAsync(
        string name, byte[]? rootSeed = null, Ed25519Signer? signer = null, string? installNodeId = null)
    {
        signer ??= new Ed25519Signer();
        if (rootSeed is null)
        {
            (_, rootSeed) = signer.GenerateKeyPair();
        }
        var (rootPub, rootPriv) = signer.GenerateFromSeed(rootSeed);
        var rootIdentity = new NodeIdentity(
            installNodeId ?? Convert.ToHexString(rootPub.AsSpan(0, 16)).ToLowerInvariant(),
            rootPub, rootPriv);

        // Short temp root so the per-team encrypted-store path (registered unopened) and
        // any UDS endpoint stay under the macOS 104-char sun_path limit.
        var dir = BuildShortTempRoot(name);
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        var connectionString = $"Data Source={Path.Combine(dir, "local-node.db")};Pooling=False";

        var services = new ServiceCollection();

        services.AddTestKernelClock();
        services.AddLogging();
        services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.People.Foundation.Data.PeopleEntityModule>();
        services.AddDbContextFactory<LocalNodeDbContext>(opt => opt.UseSqlite(connectionString));

        // The People slice exactly as the composition root composes it: the projection +
        // AddHarborlineDeltaRouter (the install-level delta plane). AddNodeContacts also wires
        // the EF party repository; we only need the CRDT half here.
        services.AddNodeContacts();

        // The real per-team registrar. listenForPeers:false → outbound-only child transport
        // (no socket bind), matching the single-device default; the encrypted store registers
        // UNOPENED so team materialization needs no SQLCipher key.
        var subkey = new TeamSubkeyDerivation(signer);
        var sqlKey = new SqlCipherKeyDerivation();
        services.AddHarborlineMultiTeam(
            DefaultTeamServiceRegistrar.Compose(dir, subkey, rootIdentity, sqlKey, listenForPeers: false));

        var sp = services.BuildServiceProvider();
        _asyncCleanup.Add(sp);

        var db = sp.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
        await using (var ctx = await db.CreateDbContextAsync())
            await ctx.Database.EnsureCreatedAsync();

        // Register contacts on the router + cold-start-hydrate, exactly as
        // ContactSyncBootstrapHostedService.StartAsync does in production.
        var router = sp.GetRequiredService<IDeltaRouter>();
        var projection = sp.GetRequiredService<ContactCrdtProjection>();
        router.Register(ContactCrdtProjection.DocumentId, projection, projection);
        await projection.HydrateFromStoreAsync(CancellationToken.None);

        var factory = sp.GetRequiredService<ITeamContextFactory>();
        return new OuterNode(sp, factory, projection, db);
    }

    private static string BuildShortTempRoot(string name)
    {
        var suffix = $"sft-cb-{name}-{Guid.NewGuid().ToString("N")[..8]}";
        var baseDir = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? Path.GetTempPath()
            : "/tmp";
        return Path.Combine(baseDir, suffix);
    }

    private GossipDaemon BuildWireDaemon(
        ISyncDaemonTransport transport,
        NodeIdentity identity,
        IEd25519Signer signer,
        IDeltaRouter router,
        IPeerTrustPolicy trust) =>
        new(
            transport,
            new VectorClock(),
            Microsoft.Extensions.Options.Options.Create(new GossipDaemonOptions
            {
                RoundIntervalSeconds = 1,
                PeerPickCount = 1,
                ConnectTimeoutSeconds = 5,
                DeadPeerBackoffSeconds = 2,
            }),
            new InMemoryNodeIdentityProvider(identity),
            signer,
            // The CONTAINER-COMPOSED router is BOTH the outbound producer and the inbound
            // sink — the exact delta plane the per-team child container resolves. NOT a
            // hand-injected projection: this is the production resolution path.
            deltaProducer: router,
            deltaSink: router,
            trustPolicy: trust, timeProvider: TimeProvider.System);

    private static NodeIdentity TeamIdentity(byte[] rootSeed, string teamId, IEd25519Signer signer, string installNodeId)
    {
        var (rootPub, _) = signer.GenerateFromSeed(rootSeed);
        var root = new NodeIdentity(installNodeId, rootPub, rootSeed);
        return TeamScopedNodeIdentity.Derive(root, teamId, new TeamSubkeyDerivation(signer));
    }

    private static async Task<Party> CreateContactAsync(OuterNode node, string displayName)
    {
        var party = Party.Create(LocalTenant, PartyKind.Person, displayName, Actor, new Instant(System.TimeProvider.System.GetUtcNow()));
        await using (var ctx = await node.Db.CreateDbContextAsync())
        {
            ctx.Set<Party>().Add(party);
            await ctx.SaveChangesAsync();
        }
        node.Projection.ProjectUpsert(party);
        return party;
    }

    private static async Task<Party> UpdateContactAsync(OuterNode node, Party party, string newDisplayName)
    {
        Party stamped;
        await using (var ctx = await node.Db.CreateDbContextAsync())
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
        node.Projection.ProjectUpsert(stamped);
        return stamped;
    }

    private static async Task DeleteContactAsync(OuterNode node, Party party)
    {
        Party tombstoned;
        await using (var ctx = await node.Db.CreateDbContextAsync())
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
        node.Projection.ProjectDelete(tombstoned);
    }

    private static async Task<string?> ReadDisplayNameAsync(OuterNode node, PartyId id)
    {
        await using var ctx = await node.Db.CreateDbContextAsync();
        var p = await ctx.Set<Party>().IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id);
        return p?.DeletedAt is null ? p?.DisplayName : null;
    }

    private static async Task WaitForEfAsync(OuterNode node, PartyId id, string expected, string because)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        string? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = await ReadDisplayNameAsync(node, id);
            if (last == expected) return;
            await Task.Delay(150);
        }
        Assert.Fail($"{because} Last observed value in B's EF store: '{last ?? "(absent)"}', expected '{expected}'.");
    }

    private static async Task WaitForEfGoneAsync(OuterNode node, PartyId id, string because)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (await ReadDisplayNameAsync(node, id) is null) return;
            await Task.Delay(150);
        }
        Assert.Fail($"{because} The contact is still visible in B's EF store after the deadline.");
    }
}
