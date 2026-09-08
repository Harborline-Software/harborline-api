using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Kernel.Crdt;
using Harborline.Api.Kernel.Crdt.Backends;
using Harborline.Api.LocalNodeHost.Data.Comms;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// Acceptance coverage for the comms append-log doctype — the FIRST messaging doctype on the live node-host
/// path. Proves the core property: two replicas, two DISTINCT authors, each appending messages, converge to
/// the SAME ordered log, with every message attributed to its author. This is the list (append-log) analogue
/// of <c>ContactCrdtConvergenceTests</c> — the contacts doctype proves map convergence; this proves
/// append-only ORDERED-list convergence.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two in-proc replicas, NO networking.</b> Convergence is proven with two in-process
/// <see cref="CommsCrdtProjection"/> replicas (each over its own CRDT engine + its own SQLite store)
/// exchanging deltas directly — simulating the gossip daemon's produce/consume round.
/// </para>
/// <para>
/// <b>Real YDotNet backend.</b> The convergence assertions exercise the production
/// <see cref="YDotNetCrdtEngine"/> (Yjs/yrs) — the stub's replay cannot honestly test concurrent-append
/// list convergence, so these tests pin the real backend.
/// </para>
/// <para>
/// <b>Two distinct authors.</b> Each author is a distinct Ed25519 keypair + a distinct member PartyId. The
/// messages they append carry their own signed identity, so a peer can tell — and verify — which author
/// wrote each message on the converged log.
/// </para>
/// </remarks>
public sealed class CommsCrdtConvergenceTests : IAsyncLifetime
{
    private readonly List<Replica> _replicas = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var r in _replicas) await r.DisposeAsync();
    }

    private const string Tenant = "team-7e57";

    // ── Two distinct authors: distinct keypair + distinct member id ──────────────────────────────────────
    private sealed class Author
    {
        public required string PartyId { get; init; }
        public required IOperationSigner Signer { get; init; }
        public required KeyPair KeyPair { get; init; }

        public static Author New(string partyId)
        {
            var kp = KeyPair.Generate();
            return new Author { PartyId = partyId, Signer = new Ed25519Signer(kp), KeyPair = kp };
        }
    }

    private static readonly Author Alice = Author.New("alice");
    private static readonly Author Bob = Author.New("bob");
    private static readonly IOperationVerifier Verifier = new Ed25519Verifier();

    // ── A small in-proc replica: own CRDT engine, own SQLite store, own projection ──────────────────────
    private sealed class Replica : IAsyncDisposable
    {
        public required string Name { get; init; }
        public required string Dir { get; init; }
        public required ServiceProvider Sp { get; init; }
        public required IDbContextFactory<NodeLocalCommsDbContext> Factory { get; init; }
        public required CommsCrdtProjection Projection { get; init; }

        public async ValueTask DisposeAsync()
        {
            await Projection.DisposeAsync();
            await Sp.DisposeAsync();
            try { if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true); }
            catch { /* best-effort temp cleanup */ }
        }
    }

    private Task<Replica> NewReplicaAsync(string name) => NewReplicaAsync(name, rosterBinding: null);

    private async Task<Replica> NewReplicaAsync(string name, Func<string, PrincipalId?>? rosterBinding)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"harborline-comms-crdt-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var connectionString = $"Data Source={Path.Combine(dir, "comms.db")};Pooling=False";

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContextFactory<NodeLocalCommsDbContext>(opt => opt.UseSqlite(connectionString));
        services.AddSingleton<ICrdtEngine, YDotNetCrdtEngine>();

        var sp = services.BuildServiceProvider();
        var factory = sp.GetRequiredService<IDbContextFactory<NodeLocalCommsDbContext>>();
        await using (var ctx = await factory.CreateDbContextAsync())
            await ctx.Database.EnsureCreatedAsync();

        // rosterBinding null = pre-enrollment merge gate (B1a integrity); non-null = forge-proof merge gate (B1b).
        var projection = new CommsCrdtProjection(
            sp.GetRequiredService<ICrdtEngine>(),
            factory,
            Verifier,
            NullLogger<CommsCrdtProjection>.Instance,
            rosterBinding);

        var replica = new Replica { Name = name, Dir = dir, Sp = sp, Factory = factory, Projection = projection };
        _replicas.Add(replica);
        return replica;
    }

    /// <summary>Append a signed message by <paramref name="author"/> the way <c>CommsRoutes</c> does
    /// (EF-first via PersistLocalAsync, then push onto the CRDT list).</summary>
    private static async Task<MessageCrdtState> AppendAsync(
        Replica r, Author author, string body, DateTimeOffset authoredAt)
    {
        var message = await CommsMessageFactory.CreateSignedAsync(
            author.Signer, author.PartyId, Tenant, body, authoredAt);
        await r.Projection.PersistLocalAsync(message, CancellationToken.None);
        r.Projection.AppendLocal(message);
        return message;
    }

    /// <summary>Read the durable EF log (the GET path's source) in authored order.</summary>
    private static Task<IReadOnlyList<MessageCrdtState>> ReadLogAsync(Replica r) =>
        r.Projection.ReadLogAsync(Tenant, CancellationToken.None);

    /// <summary>One direction of a sync round on the LIVE trigger: encode src's outbound delta against
    /// dst's clock, apply it to dst, then await the reconciles the projection's Changed→reconcile handler
    /// spawned (the production path — no explicit ReconcileAsync).</summary>
    private static async Task SyncAsync(Replica src, Replica dst)
    {
        var delta = await src.Projection.EncodeOutboundDeltaAsync(
            CommsCrdtProjection.DocumentId, dst.Projection.VectorClock, CancellationToken.None);
        Assert.NotNull(delta);
        await dst.Projection.ApplyInboundDeltaAsync(
            CommsCrdtProjection.DocumentId, 1, delta!.Value, CancellationToken.None);
        await dst.Projection.DrainPendingReconcilesAsync();
    }

    /// <summary>The full bidirectional anti-entropy round: each encodes against the OTHER's pre-exchange
    /// clock, both apply, both reconcile — the order-independent convergence both replicas reach.</summary>
    private static async Task ExchangeAsync(Replica a, Replica b)
    {
        var aClock = a.Projection.VectorClock;
        var bClock = b.Projection.VectorClock;
        var aDelta = await a.Projection.EncodeOutboundDeltaAsync(CommsCrdtProjection.DocumentId, bClock, CancellationToken.None);
        var bDelta = await b.Projection.EncodeOutboundDeltaAsync(CommsCrdtProjection.DocumentId, aClock, CancellationToken.None);
        await b.Projection.ApplyInboundDeltaAsync(CommsCrdtProjection.DocumentId, 1, aDelta!.Value, CancellationToken.None);
        await a.Projection.ApplyInboundDeltaAsync(CommsCrdtProjection.DocumentId, 1, bDelta!.Value, CancellationToken.None);
        await a.Projection.DrainPendingReconcilesAsync();
        await b.Projection.DrainPendingReconcilesAsync();
    }

    private static IReadOnlyList<(string Author, string Body)> OrderedAuthorBodies(IReadOnlyList<MessageCrdtState> log) =>
        log.Select(m => (m.AuthorPartyId, m.Body)).ToList();

    // ── Test 1: one author appends on A → message reaches B's readable log, attributed ──────────────────

    [Fact(DisplayName = "comms: append on A → delta → appears in B's readable log, attributed to its author")]
    public async Task Append_On_A_Appears_On_B_Attributed()
    {
        var a = await NewReplicaAsync("A");
        var b = await NewReplicaAsync("B");

        var t0 = DateTimeOffset.UtcNow;
        var msg = await AppendAsync(a, Alice, "hello team", t0);

        await SyncAsync(a, b);

        var bLog = await ReadLogAsync(b);
        Assert.Single(bLog);
        Assert.Equal("hello team", bLog[0].Body);
        // Attributed to its author (Alice's member id + Alice's signing key), and the signature verifies.
        Assert.Equal("alice", bLog[0].AuthorPartyId);
        Assert.Equal(msg.AuthorIssuerId, bLog[0].AuthorIssuerId);
        Assert.True(CommsMessageFactory.VerifyAuthorship(bLog[0], Verifier));
    }

    // ── Test 2: TWO DISTINCT AUTHORS converge in append-order, each attributed (the core proof) ─────────

    [Fact(DisplayName = "comms: two distinct authors converge to the SAME ordered log, each attributed (CORE)")]
    public async Task Two_Distinct_Authors_Converge_In_Append_Order_Attributed()
    {
        var a = await NewReplicaAsync("A");
        var b = await NewReplicaAsync("B");

        // Alice appends on A; Bob appends on B — concurrently, neither seen by the other yet.
        var t0 = DateTimeOffset.UtcNow;
        var aliceMsg = await AppendAsync(a, Alice, "Alice: standup at 10?", t0);
        var bobMsg   = await AppendAsync(b, Bob,   "Bob: works for me",     t0.AddSeconds(1));

        // Each replica sees only its own message so far.
        Assert.Single(await ReadLogAsync(a));
        Assert.Single(await ReadLogAsync(b));

        // Full bidirectional round.
        await ExchangeAsync(a, b);

        var aLog = await ReadLogAsync(a);
        var bLog = await ReadLogAsync(b);

        // CONVERGENCE: both replicas hold BOTH messages (the union — no append lost).
        Assert.Equal(2, aLog.Count);
        Assert.Equal(2, bLog.Count);

        // The CRDT list converged to the SAME deterministic total order on both replicas.
        var aOrder = a.Projection.Snapshot().Select(m => m.MessageId).ToList();
        var bOrder = b.Projection.Snapshot().Select(m => m.MessageId).ToList();
        Assert.Equal(aOrder, bOrder); // same order on both — CRDT-list convergence

        // ATTRIBUTION: the two distinct authors are distinguishable on the converged log. Each message
        // carries its own author's member id + signing key, and each signature verifies against that key.
        var byId = aLog.ToDictionary(m => m.MessageId);
        Assert.Equal("alice", byId[aliceMsg.MessageId].AuthorPartyId);
        Assert.Equal("bob",   byId[bobMsg.MessageId].AuthorPartyId);
        Assert.NotEqual(byId[aliceMsg.MessageId].AuthorIssuerId, byId[bobMsg.MessageId].AuthorIssuerId);
        Assert.All(aLog, m => Assert.True(CommsMessageFactory.VerifyAuthorship(m, Verifier)));
        Assert.All(bLog, m => Assert.True(CommsMessageFactory.VerifyAuthorship(m, Verifier)));
    }

    // ── Test 3: concurrent interleaved appends from both authors converge deterministically ─────────────

    [Fact(DisplayName = "comms: interleaved concurrent appends from both authors converge deterministically")]
    public async Task Interleaved_Concurrent_Appends_Converge_Deterministically()
    {
        var a = await NewReplicaAsync("A");
        var b = await NewReplicaAsync("B");

        var t0 = DateTimeOffset.UtcNow;
        // A burst of concurrent appends on BOTH replicas before any exchange — the interleave the CRDT must
        // resolve to one deterministic order.
        await AppendAsync(a, Alice, "A1", t0);
        await AppendAsync(b, Bob,   "B1", t0.AddMilliseconds(5));
        await AppendAsync(a, Alice, "A2", t0.AddMilliseconds(10));
        await AppendAsync(b, Bob,   "B2", t0.AddMilliseconds(15));
        await AppendAsync(a, Alice, "A3", t0.AddMilliseconds(20));

        await ExchangeAsync(a, b);

        var aSnap = a.Projection.Snapshot();
        var bSnap = b.Projection.Snapshot();

        // Both replicas hold all 5 messages and AGREE on the order (deterministic convergence).
        Assert.Equal(5, aSnap.Count);
        Assert.Equal(5, bSnap.Count);
        Assert.Equal(aSnap.Select(m => m.MessageId), bSnap.Select(m => m.MessageId));

        // The EF read models also converge to the same set (authored order in the read model).
        var aBodies = (await ReadLogAsync(a)).Select(m => m.Body).ToHashSet();
        var bBodies = (await ReadLogAsync(b)).Select(m => m.Body).ToHashSet();
        Assert.Equal(new[] { "A1", "A2", "A3", "B1", "B2" }.ToHashSet(), aBodies);
        Assert.Equal(aBodies, bBodies);
    }

    // ── Test 4: append-only — applying the same inbound delta twice is idempotent (no duplicate row) ────

    [Fact(DisplayName = "comms: applying the same inbound delta twice is idempotent (no duplicate message)")]
    public async Task Delta_Apply_Is_Idempotent()
    {
        var a = await NewReplicaAsync("A");
        var b = await NewReplicaAsync("B");

        var msg = await AppendAsync(a, Alice, "once", DateTimeOffset.UtcNow);
        var delta = await a.Projection.EncodeOutboundDeltaAsync(
            CommsCrdtProjection.DocumentId, b.Projection.VectorClock, CancellationToken.None);

        await b.Projection.ApplyInboundDeltaAsync(CommsCrdtProjection.DocumentId, 1, delta!.Value, CancellationToken.None);
        await b.Projection.ApplyInboundDeltaAsync(CommsCrdtProjection.DocumentId, 2, delta!.Value, CancellationToken.None);
        await b.Projection.DrainPendingReconcilesAsync();

        Assert.Equal(1, b.Projection.Count);
        var bLog = await ReadLogAsync(b);
        Assert.Single(bLog);
        Assert.Equal(msg.MessageId, bLog[0].MessageId);
    }

    // ── Test 5: signature proves message INTEGRITY (a mutated signed message fails) ──────────────────────
    // NOTE: this proves INTEGRITY only — that you cannot MUTATE an already-signed message. It does NOT prove
    // the AuthorPartyId↔AuthorIssuerId binding on its own (the integrity-only check); the party↔key binding is
    // proven forge-proof by the roster-aware overload, covered by Forged_Foreign_Party_Is_Rejected_By_Roster_Binding
    // (B1b CLOSED) below.

    [Fact(DisplayName = "comms: mutating an already-signed message fails verification (INTEGRITY only)")]
    public async Task Mutated_Signed_Message_Fails_Verification()
    {
        var a = await NewReplicaAsync("A");
        var msg = await AppendAsync(a, Alice, "authentic body", DateTimeOffset.UtcNow);

        Assert.True(CommsMessageFactory.VerifyAuthorship(msg, Verifier));

        // Tamper the body (record with-expression) — the signature no longer covers it.
        var tampered = msg with { Body = "forged body" };
        Assert.False(CommsMessageFactory.VerifyAuthorship(tampered, Verifier));

        // Re-attributing an ALREADY-SIGNED message to a DIFFERENT partyId without re-signing also fails
        // (partyId is inside the signed payload). NOTE: this is NOT the same as forging — see the
        // foreign-party FRESH-sign test below, which DOES pass (the known enrollment gap).
        var misattributed = msg with { AuthorPartyId = "bob" };
        Assert.False(CommsMessageFactory.VerifyAuthorship(misattributed, Verifier));
    }

    // ── Test 5b: VERIFY-IS-ENFORCED-ON-MERGE — an invalid-signature message is DROPPED (#1277 B1a) ───────
    // This is the honest replacement for the false "misattribution fails verification" claim. It proves the
    // gate that actually matters: a garbage/unsigned peer message is NOT stored, NOT returned by the read
    // path. Fails PRE-FIX (before this PR, reconcile did zero verification → the message landed in EF).

    [Fact(DisplayName = "comms: an INVALID-signature peer message is DROPPED on merge — not stored, not in GET (B1a)")]
    public async Task Invalid_Signature_Peer_Message_Is_Dropped_On_Merge()
    {
        var src = await NewReplicaAsync("src");
        var dst = await NewReplicaAsync("dst");

        // A legitimately-signed message (valid sig for its stamped key) — the legit path: this MUST merge.
        var good = await AppendAsync(src, Alice, "legit message", DateTimeOffset.UtcNow);

        // A garbage-signed message: a real Alice-signed message whose BODY was then mutated, so its signature
        // no longer validates. Push it straight onto src's CRDT list (the source of outbound deltas) WITHOUT a
        // local EF write — exactly what a malicious/garbage peer delta looks like on the wire.
        var authentic = await CommsMessageFactory.CreateSignedAsync(
            Alice.Signer, Alice.PartyId, Tenant, "authentic", DateTimeOffset.UtcNow);
        var garbage = authentic with { Body = "tampered-after-signing", MessageId = Guid.NewGuid().ToString("D") };
        Assert.False(CommsMessageFactory.VerifyAuthorship(garbage, Verifier)); // sanity: it does NOT verify
        src.Projection.AppendLocal(garbage); // place it on src's list so it ships in the delta

        // Sync src → dst over the LIVE merge→reconcile trigger (the production path).
        await SyncAsync(src, dst);

        // The CRDT list converged (merge-everything) — dst's list holds BOTH (convergence is by design)...
        Assert.Equal(2, dst.Projection.Count);

        // ...but the durable read store + GET path are GATED: only the valid message landed; the garbage one
        // was DROPPED on merge (verify-on-merge enforcement). PRE-FIX this asserted 2 — the garbage row was
        // stored.
        var dstLog = await ReadLogAsync(dst);
        Assert.Single(dstLog);
        Assert.Equal(good.MessageId, dstLog[0].MessageId);
        Assert.Equal("legit message", dstLog[0].Body);
        Assert.DoesNotContain(dstLog, m => m.MessageId == garbage.MessageId);
    }

    // ── Test 5c: FORGE-PROOF ATTRIBUTION — the foreign-party forgery is now REJECTED (#1277 B1b CLOSED) ───
    // THE PAYOFF OF THE ROSTER (enrollment Phase A). An attacker signs a FRESH message with their OWN key while
    // stamping a VICTIM's partyId. PRE-enrollment this PASSED (the signature is valid for the attacker's stamped
    // key, and nothing bound key→party). Now the roster binds alice→Alice's key + bob→Bob's key, and the
    // forge-proof overload checks the message's stamped key AGAINST that binding for the claimed party — so
    // Bob's key signing under "alice" FAILS (Bob's key ≠ Alice's roster-bound key). This flips the old
    // "still passes" gap test into the enforcement regression test that proves the forgery is rejected.

    [Fact(DisplayName = "comms: a foreign-party FORGED message is REJECTED by forge-proof attribution (B1b CLOSED)")]
    public async Task Forged_Foreign_Party_Is_Rejected_By_Roster_Binding()
    {
        // The verified roster's party→pubkey binding (what MemberRoster.PublicKeyOf returns): alice→Alice's key,
        // bob→Bob's key. A party not in this map has no binding → fail-closed.
        PrincipalId? RosterBinding(string partyId) => partyId switch
        {
            "alice" => Alice.KeyPair.PrincipalId,
            "bob" => Bob.KeyPair.PrincipalId,
            _ => null,
        };

        // Attacker = Bob's keypair (his own valid signing key), but he stamps Alice's partyId.
        var forged = await CommsMessageFactory.CreateSignedAsync(
            Bob.Signer, authorPartyId: Alice.PartyId, tenantId: Tenant, body: "I am Alice (forged)",
            authoredAt: DateTimeOffset.UtcNow);

        // Sanity: the forgery is integrity-valid (Bob's key DID validly sign it) — so the OLD integrity-only
        // check still passes. That is exactly why integrity alone was theater (#1277 B1b).
        Assert.True(CommsMessageFactory.VerifyAuthorship(forged, Verifier));

        // THE PROOF: the forge-proof overload REJECTS it — the stamped issuer (Bob's key) is NOT the roster's
        // bound key for the claimed party (alice → Alice's key). #1277 B1 CLOSED.
        Assert.False(CommsMessageFactory.VerifyAuthorship(forged, Verifier, RosterBinding));

        // And a LEGITIMATE message — Alice signing as alice with her own (roster-bound) key — still PASSES the
        // forge-proof check (the gate doesn't break honest attribution).
        var legit = await CommsMessageFactory.CreateSignedAsync(
            Alice.Signer, authorPartyId: Alice.PartyId, tenantId: Tenant, body: "I really am Alice",
            authoredAt: DateTimeOffset.UtcNow);
        Assert.True(CommsMessageFactory.VerifyAuthorship(legit, Verifier, RosterBinding));

        // An UN-ENROLLED author (no roster binding) is also rejected fail-closed — you cannot attribute a
        // message to a party who is not a verified member.
        var unenrolled = await CommsMessageFactory.CreateSignedAsync(
            Bob.Signer, authorPartyId: "stranger", tenantId: Tenant, body: "hi", authoredAt: DateTimeOffset.UtcNow);
        Assert.False(CommsMessageFactory.VerifyAuthorship(unenrolled, Verifier, RosterBinding));
    }

    // ── Test 5d: FORGE-PROOF ON THE LIVE MERGE PATH — the forgery is DROPPED end-to-end (B1b CLOSED) ──────
    // The unit check above proves the gate logic; this proves it ENFORCED on the production merge→reconcile
    // path when a roster binding is wired into the projection. A forged peer message (Bob's key claiming
    // Alice's partyId) ships over the wire, the CRDT converges (merge-everything by design), but the
    // forge-proof verify-on-merge gate DROPS it — it never lands in the EF read store / GET. A legitimate
    // Alice message in the same round IS stored.

    [Fact(DisplayName = "comms: a forged-party peer message is DROPPED on the live merge path (forge-proof, B1b)")]
    public async Task Forged_Peer_Message_Is_Dropped_On_Merge_With_Roster()
    {
        PrincipalId? RosterBinding(string partyId) => partyId switch
        {
            "alice" => Alice.KeyPair.PrincipalId,
            "bob" => Bob.KeyPair.PrincipalId,
            _ => null,
        };

        var src = await NewReplicaAsync("src");                      // attacker's source (no gate needed to emit)
        var dst = await NewReplicaAsync("dst", RosterBinding);       // victim's node — forge-proof merge gate

        // A legitimate Alice message (alice signed with Alice's roster-bound key) — MUST survive the gate.
        var good = await AppendAsync(src, Alice, "legit from alice", DateTimeOffset.UtcNow);

        // The forgery: Bob's key signs a FRESH message stamping Alice's partyId. Push straight onto src's CRDT
        // list (the source of outbound deltas) — exactly what a malicious peer delta looks like.
        var forged = await CommsMessageFactory.CreateSignedAsync(
            Bob.Signer, authorPartyId: Alice.PartyId, tenantId: Tenant, body: "I am Alice (forged)",
            authoredAt: DateTimeOffset.UtcNow);
        Assert.True(CommsMessageFactory.VerifyAuthorship(forged, Verifier));                  // integrity-valid...
        Assert.False(CommsMessageFactory.VerifyAuthorship(forged, Verifier, RosterBinding));  // ...but a forgery
        src.Projection.AppendLocal(forged);

        await SyncAsync(src, dst);

        // The CRDT list converged (both items present — merge-everything)...
        Assert.Equal(2, dst.Projection.Count);

        // ...but the durable read store + GET are GATED by forge-proof attribution: only Alice's legitimate
        // message landed; the forgery was DROPPED on merge. #1277 B1 CLOSED on the live path.
        var dstLog = await ReadLogAsync(dst);
        Assert.Single(dstLog);
        Assert.Equal(good.MessageId, dstLog[0].MessageId);
        Assert.DoesNotContain(dstLog, m => m.MessageId == forged.MessageId);
    }

    // ── Test 6: cold-start hydration re-seeds the CRDT list from the durable store ──────────────────────

    [Fact(DisplayName = "comms: cold-start hydration re-seeds the CRDT list from the recoverable store")]
    public async Task Hydration_Reseeds_From_Durable_Store()
    {
        var a = await NewReplicaAsync("A");

        // Append two messages (EF + CRDT), then build a FRESH projection over the SAME EF store and hydrate.
        var t0 = DateTimeOffset.UtcNow;
        await AppendAsync(a, Alice, "persisted 1", t0);
        await AppendAsync(a, Bob,   "persisted 2", t0.AddSeconds(1));

        await using var fresh = new CommsCrdtProjection(
            new YDotNetCrdtEngine(), a.Factory, Verifier, NullLogger<CommsCrdtProjection>.Instance);
        Assert.Equal(0, fresh.Count); // empty before hydration

        var hydrated = await fresh.HydrateFromStoreAsync(CancellationToken.None);
        Assert.Equal(2, hydrated);
        Assert.Equal(2, fresh.Count); // the durable rows re-seeded the CRDT list

        // And the hydrated list still verifies authorship (the signed identity round-tripped through EF).
        Assert.All(fresh.Snapshot(), m => Assert.True(CommsMessageFactory.VerifyAuthorship(m, Verifier)));
    }
}
