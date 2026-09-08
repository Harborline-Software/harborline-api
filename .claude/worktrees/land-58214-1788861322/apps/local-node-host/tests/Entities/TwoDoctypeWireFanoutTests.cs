using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.Kernel.Crdt;
using Harborline.Api.Kernel.Crdt.Backends;
using Harborline.Api.Kernel.Security.Crypto;
using Harborline.Api.Kernel.Security.Keys;
using Harborline.Api.Kernel.Sync.Application;
using Harborline.Api.Kernel.Sync.Gossip;
using Harborline.Api.Kernel.Sync.Handshake;
using Harborline.Api.Kernel.Sync.Identity;
using Harborline.Api.Kernel.Sync.Protocol;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Comms;
using Harborline.Api.LocalNodeHost.Data.People;

// Two Ed25519Signer types are in scope: the daemon's transport signer
// (Harborline.Api.Kernel.Security.Crypto) and the comms message signer
// (Harborline.Api.Foundation.Crypto). Alias BOTH to avoid the ambiguous-reference
// collision (the unqualified name is ambiguous before overload resolution).
using TransportSigner = Harborline.Api.Kernel.Security.Crypto.Ed25519Signer;
using CommsSigner = Harborline.Api.Foundation.Crypto.Ed25519Signer;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// GAP-1 HEADLINE acceptance — per-document-id gossip wire FAN-OUT: a SECOND doctype crosses the wire.
/// Two <see cref="GossipDaemon"/> instances over REAL loopback TCP, each whose delta plane is the
/// install-level <see cref="DeltaRoutingRegistry"/> with BOTH a contacts projection AND a comms projection
/// registered (the production object graph — the container bridge wires this exact router). A contact CREATE
/// AND a comms message authored on node A BOTH converge to node B — each routed to its own projection.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this bites pre-fix.</b> Before per-id fan-out, the daemon hard-coded a single <c>"default"</c>
/// outbound stream id (<c>GossipDaemon</c> phase-1 single-stream), which the router resolves to the
/// FIRST-registered doctype (contacts). The comms projection was registered on the router but the daemon
/// never iterated <see cref="DeltaRoutingRegistry.RegisteredDocumentIds"/>, so the comms delta NEVER crossed
/// the wire — only contacts converged. <see cref="Both_Contacts_And_Comms_Cross_The_Wire_Two_Doctypes"/>
/// asserts the comms message reaches B; that assertion FAILS pre-fix (comms never shipped) and PASSES now
/// (the daemon ships one DELTA_STREAM per registered doc-id, each routed by its StreamId). The contacts
/// assertion holds in BOTH worlds, isolating the fan-out as the thing that carries the second doctype.
/// </para>
/// <para>
/// <b>Same hardened channel.</b> Both daemons run the LIVE shared-root trust gate + DoS caps + rate limiter;
/// the fan-out adds per-id streams over that one authenticated/capped channel — no new unauthenticated path.
/// </para>
/// </remarks>
public sealed class TwoDoctypeWireFanoutTests : IAsyncLifetime
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
    private const string CommsTenant = "local";
    private static readonly IOperationVerifier Verifier = new Ed25519Verifier();

    [Fact(DisplayName =
        "GAP-1: BOTH a contact AND a comms message authored on A converge to B — two doctypes cross the wire (fan-out)")]
    public async Task Both_Contacts_And_Comms_Cross_The_Wire_Two_Doctypes()
    {
        var signer = new TransportSigner();
        var (_, sharedRoot) = signer.GenerateKeyPair();

        // Two devices, ONE shared root seed → SAME team key (trust anchor), DISTINCT install node ids.
        var deviceA = TeamIdentity(sharedRoot, "team-home", signer, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var deviceB = TeamIdentity((byte[])sharedRoot.Clone(), "team-home", signer, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        Assert.Equal(deviceA.PublicKey, deviceB.PublicKey);

        var a = await NewReplicaAsync("A");
        var b = await NewReplicaAsync("B");

        // Each replica's delta plane is the install-level router with BOTH doctypes registered, contacts
        // FIRST (so contacts is the default route) and comms SECOND (the additive #1265 registration). This
        // is the production graph the container bridge resolves — NOT a hand-injected single projection.
        Assert.Equal(ContactCrdtProjection.DocumentId, a.Router!.DefaultEntry);
        Assert.Contains(ContactCrdtProjection.DocumentId, a.Router.RegisteredDocumentIds);
        Assert.Contains(CommsCrdtProjection.DocumentId, a.Router.RegisteredDocumentIds);
        Assert.Equal(2, a.Router.RegisteredDocumentIds.Count); // two doctypes — the fan-out cardinality

        // B listens; B's accept loop runs the LIVE shared-root trust gate and feeds inbound deltas to B's
        // router (which routes each by StreamId to the right projection).
        var transportB = new TcpSyncDaemonTransport("tcp://127.0.0.1:0");
        _cleanup.Add(transportB);
        var endpointB = transportB.ListenEndpoint!;
        var daemonB = BuildDaemon(transportB, deviceB, signer, b.Router!, new SharedRootTrustPolicy(deviceB.PublicKey));
        _cleanup.Add(daemonB);
        await daemonB.StartListeningAsync(CancellationToken.None);

        // A initiates; A's daemon ships one DELTA_STREAM per registered doc-id each round.
        var transportA = new TcpSyncDaemonTransport();
        _cleanup.Add(transportA);
        var daemonA = BuildDaemon(transportA, deviceA, signer, a.Router!, new SharedRootTrustPolicy(deviceA.PublicKey));
        _cleanup.Add(daemonA);
        daemonA.AddPeer(endpointB, deviceB.PublicKey);
        await daemonA.StartAsync(CancellationToken.None);

        // ── Author BOTH doctypes on A ────────────────────────────────────────
        // A contact (doctype #1 — the one that already crossed pre-fix).
        var party = await CreateContactAsync(a, "Ada Lovelace");
        // A comms message (doctype #2 — the one the fan-out now carries).
        var message = await AppendCommsAsync(a, "alice", "hello team — first cross-wire message");

        // ── Doctype #1 converges (holds in BOTH pre- and post-fix) ───────────
        await WaitForContactAsync(b, party.Id, expected: "Ada Lovelace",
            because: "a contact CREATEd on A must reach B over the live daemon (doctype #1, the default route).");

        // ── Doctype #2 converges — THE FAN-OUT PROOF (FAILS pre-fix) ─────────
        await WaitForCommsAsync(b, message.MessageId, expectedBody: "hello team — first cross-wire message",
            because: "the comms message must ALSO cross the wire — per-id fan-out ships a SECOND DELTA_STREAM "
                   + "tagged \"comms\" that B routes to its comms projection. Pre-fix only \"default\"=contacts "
                   + "shipped, so this never converged.");

        // B's comms message is attributed to its author and verifies (the signed identity round-tripped over
        // the wire through the comms sink — proof it really traversed the comms route, not contacts).
        var bLog = await b.Comms!.ReadLogAsync(CommsTenant, CancellationToken.None);
        var landed = Assert.Single(bLog, m => m.MessageId == message.MessageId);
        Assert.Equal("alice", landed.AuthorPartyId);
        Assert.True(CommsMessageFactory.VerifyAuthorship(landed, Verifier));

        await daemonA.StopAsync(CancellationToken.None);
        await daemonB.StopListeningAsync(CancellationToken.None);
    }

    [Fact(DisplayName =
        "GAP-1 back-compat: a 'default'-only (non-fan-out) peer still converges contacts — single-stream wire still works")]
    public async Task Default_Only_Peer_Still_Converges_Contacts_Back_Compat()
    {
        var signer = new TransportSigner();
        var (_, sharedRoot) = signer.GenerateKeyPair();
        var deviceA = TeamIdentity(sharedRoot, "team-home", signer, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var deviceB = TeamIdentity((byte[])sharedRoot.Clone(), "team-home", signer, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");

        // B is a NON-FAN-OUT peer: its delta plane is a BARE contacts projection (NOT an IDeltaRouter), so it
        // ships/understands exactly one "default" stream — the legacy single-stream wire shape. A is a
        // fan-out peer (router with contacts registered). They must still converge contacts.
        var a = await NewReplicaAsync("A");
        var bLegacy = await NewLegacyReplicaAsync("B");

        var transportB = new TcpSyncDaemonTransport("tcp://127.0.0.1:0");
        _cleanup.Add(transportB);
        var endpointB = transportB.ListenEndpoint!;
        // B's daemon uses the BARE projection as producer+sink (the pre-router, single-stream posture).
        var daemonB = BuildDaemonBare(transportB, deviceB, signer, bLegacy.Contacts, new SharedRootTrustPolicy(deviceB.PublicKey));
        _cleanup.Add(daemonB);
        await daemonB.StartListeningAsync(CancellationToken.None);

        var transportA = new TcpSyncDaemonTransport();
        _cleanup.Add(transportA);
        var daemonA = BuildDaemon(transportA, deviceA, signer, a.Router!, new SharedRootTrustPolicy(deviceA.PublicKey));
        _cleanup.Add(daemonA);
        daemonA.AddPeer(endpointB, deviceB.PublicKey);
        await daemonA.StartAsync(CancellationToken.None);

        var party = await CreateContactAsync(a, "Grace Hopper");

        await WaitForContactAsync(bLegacy, party.Id, expected: "Grace Hopper",
            because: "a fan-out peer must still converge contacts to a single-stream 'default'-only peer (back-compat).");

        await daemonA.StopAsync(CancellationToken.None);
        await daemonB.StopListeningAsync(CancellationToken.None);
    }

    // ───────────────────────────── helpers ─────────────────────────────

    private GossipDaemon BuildDaemon(
        ISyncDaemonTransport transport, NodeIdentity identity, IEd25519Signer signer,
        IDeltaRouter router, IPeerTrustPolicy trust) =>
        new(
            transport,
            new VectorClock(),
            Options.Create(new GossipDaemonOptions
            {
                RoundIntervalSeconds = 1, PeerPickCount = 1, ConnectTimeoutSeconds = 5, DeadPeerBackoffSeconds = 2,
            }),
            new InMemoryNodeIdentityProvider(identity),
            signer,
            // The install-level ROUTER is BOTH outbound producer and inbound sink — the container-bridge graph.
            // The daemon iterates RegisteredDocumentIds for the per-id fan-out.
            deltaProducer: router,
            deltaSink: router,
            trustPolicy: trust, timeProvider: TimeProvider.System);

    private GossipDaemon BuildDaemonBare(
        ISyncDaemonTransport transport, NodeIdentity identity, IEd25519Signer signer,
        ContactCrdtProjection contacts, IPeerTrustPolicy trust) =>
        new(
            transport,
            new VectorClock(),
            Options.Create(new GossipDaemonOptions
            {
                RoundIntervalSeconds = 1, PeerPickCount = 1, ConnectTimeoutSeconds = 5, DeadPeerBackoffSeconds = 2,
            }),
            new InMemoryNodeIdentityProvider(identity),
            signer,
            // A BARE single-document projection (NOT an IDeltaRouter) — the legacy single-stream posture: the
            // daemon ships exactly one "default" frame and applies the inbound "default" frame to this doc.
            deltaProducer: contacts,
            deltaSink: contacts,
            trustPolicy: trust, timeProvider: TimeProvider.System);

    private static NodeIdentity TeamIdentity(byte[] rootSeed, string teamId, IEd25519Signer signer, string installNodeId)
    {
        var (rootPub, _) = signer.GenerateFromSeed(rootSeed);
        var root = new NodeIdentity(installNodeId, rootPub, rootSeed);
        return TeamScopedNodeIdentity.Derive(root, teamId, new TeamSubkeyDerivation(signer));
    }

    /// <summary>A replica whose delta plane is the install-level router with BOTH doctypes registered.</summary>
    private async Task<Replica> NewReplicaAsync(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"harborline-fanout-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);

        // Contacts + comms each over their own SQLite store + their own CRDT engine.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.People.Foundation.Data.PeopleEntityModule>();
        services.AddDbContextFactory<LocalNodeDbContext>(o => o.UseSqlite($"Data Source={Path.Combine(dir, "contacts.db")};Pooling=False"));
        services.AddDbContextFactory<NodeLocalCommsDbContext>(o => o.UseSqlite($"Data Source={Path.Combine(dir, "comms.db")};Pooling=False"));

        var sp = services.BuildServiceProvider();
        var contactsFactory = sp.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
        var commsFactory = sp.GetRequiredService<IDbContextFactory<NodeLocalCommsDbContext>>();
        await using (var ctx = await contactsFactory.CreateDbContextAsync()) await ctx.Database.EnsureCreatedAsync();
        await using (var ctx = await commsFactory.CreateDbContextAsync()) await ctx.Database.EnsureCreatedAsync();

        // DISTINCT CRDT engines per doctype (the production graph — contacts and comms each own a document).
        var contacts = new ContactCrdtProjection(
            new YDotNetCrdtEngine(), contactsFactory, NullLogger<ContactCrdtProjection>.Instance);
        var comms = new CommsCrdtProjection(
            new YDotNetCrdtEngine(), commsFactory, Verifier, NullLogger<CommsCrdtProjection>.Instance);

        // The install-level router: contacts FIRST (default route), comms SECOND (additive #1265 registration).
        var router = new DeltaRoutingRegistry();
        router.Register(ContactCrdtProjection.DocumentId, contacts, contacts);
        router.Register(CommsCrdtProjection.DocumentId, comms, comms);

        var replica = new Replica
        {
            Name = name, Dir = dir, Sp = sp, ContactsFactory = contactsFactory, CommsFactory = commsFactory,
            Contacts = contacts, Comms = comms, Router = router,
        };
        _cleanup.Add(replica);
        return replica;
    }

    /// <summary>A legacy replica whose delta plane is a BARE contacts projection (no router, single-stream).</summary>
    private async Task<Replica> NewLegacyReplicaAsync(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"harborline-fanout-legacy-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.People.Foundation.Data.PeopleEntityModule>();
        services.AddDbContextFactory<LocalNodeDbContext>(o => o.UseSqlite($"Data Source={Path.Combine(dir, "contacts.db")};Pooling=False"));

        var sp = services.BuildServiceProvider();
        var contactsFactory = sp.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
        await using (var ctx = await contactsFactory.CreateDbContextAsync()) await ctx.Database.EnsureCreatedAsync();

        var contacts = new ContactCrdtProjection(
            new YDotNetCrdtEngine(), contactsFactory, NullLogger<ContactCrdtProjection>.Instance);

        var replica = new Replica
        {
            Name = name, Dir = dir, Sp = sp, ContactsFactory = contactsFactory, CommsFactory = null,
            Contacts = contacts, Comms = null, Router = null,
        };
        _cleanup.Add(replica);
        return replica;
    }

    private static async Task<Party> CreateContactAsync(Replica r, string displayName)
    {
        var party = Party.Create(LocalTenant, PartyKind.Person, displayName, Actor, new Instant(System.TimeProvider.System.GetUtcNow()));
        await using (var ctx = await r.ContactsFactory.CreateDbContextAsync())
        {
            ctx.Set<Party>().Add(party);
            await ctx.SaveChangesAsync();
        }
        r.Contacts.ProjectUpsert(party);
        return party;
    }

    private async Task<MessageCrdtState> AppendCommsAsync(Replica r, string authorPartyId, string body)
    {
        var signer = new CommsSigner(KeyPair.Generate());
        var message = await CommsMessageFactory.CreateSignedAsync(
            signer, authorPartyId, CommsTenant, body, DateTimeOffset.UtcNow);
        await r.Comms!.PersistLocalAsync(message, CancellationToken.None);
        r.Comms.AppendLocal(message);
        return message;
    }

    private static async Task<string?> ReadContactNameAsync(Replica r, PartyId id)
    {
        await using var ctx = await r.ContactsFactory.CreateDbContextAsync();
        var p = await ctx.Set<Party>().IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        return p?.DeletedAt is null ? p?.DisplayName : null;
    }

    private static async Task WaitForContactAsync(Replica r, PartyId id, string expected, string because)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        string? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = await ReadContactNameAsync(r, id);
            if (last == expected) return;
            await Task.Delay(150);
        }
        Assert.Fail($"{because} Last observed contact in B's store: '{last ?? "(absent)"}', expected '{expected}'.");
    }

    private static async Task WaitForCommsAsync(Replica r, string messageId, string expectedBody, string because)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        string? last = null;
        while (DateTime.UtcNow < deadline)
        {
            var log = await r.Comms!.ReadLogAsync(CommsTenant, CancellationToken.None);
            var hit = log.FirstOrDefault(m => m.MessageId == messageId);
            if (hit is not null) { if (hit.Body == expectedBody) return; last = hit.Body; }
            await Task.Delay(150);
        }
        Assert.Fail($"{because} Comms message '{messageId}' in B's log: '{last ?? "(absent)"}', expected '{expectedBody}'.");
    }

    private sealed class Replica : IAsyncDisposable
    {
        public required string Name { get; init; }
        public required string Dir { get; init; }
        public required ServiceProvider Sp { get; init; }
        public required IDbContextFactory<LocalNodeDbContext> ContactsFactory { get; init; }
        public required IDbContextFactory<NodeLocalCommsDbContext>? CommsFactory { get; init; }
        public required ContactCrdtProjection Contacts { get; init; }
        public required CommsCrdtProjection? Comms { get; init; }
        public required DeltaRoutingRegistry? Router { get; init; }

        public async ValueTask DisposeAsync()
        {
            await Contacts.DisposeAsync();
            if (Comms is not null) await Comms.DisposeAsync();
            await Sp.DisposeAsync();
        }
    }
}
