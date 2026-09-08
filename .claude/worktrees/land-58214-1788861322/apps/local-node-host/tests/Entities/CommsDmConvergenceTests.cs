using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.IO;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Kernel.Crdt;
using Harborline.Api.Kernel.Crdt.Backends;
using Harborline.Api.LocalNodeHost.Data.Comms;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// C4 — the A↔B DM seal CONSTRUCTION + convergence proof (the Tier-1 backend analogue of the canonical E2E A↔B
/// DM scenario), exercised with a clearly-labeled TEST-DOUBLE key resolver. Two in-process replicas (Alice +
/// Bob), each with its OWN CRDT engine + SQLite store + a per-conversation projection, converge a 1:1 SEALED DM
/// addressed by the deterministic <c>dm:</c> id — scoped, so the DM does NOT bleed into the team channel. Plus a
/// THIRD replica (Carol) acting AS an HONEST non-participant: the sealed delta reaches her node (no C5 routing
/// yet) but her test-double resolver's self-guard derives no key, so she holds only ciphertext.
/// </summary>
/// <remarks>
/// <para>
/// <b>SCOPE — this proves the CONSTRUCTION, not the production confidentiality GUARANTEE</b> (sec-eng deep-review
/// of PR #1325). These tests use <see cref="SeedDerivedParticipantDmKeyResolver"/>, a TEST-ONLY double that derives
/// keys from a party id — so they demonstrate seal/unseal/AAD/tamper/isolation round-trips and the
/// honest-non-participant routing guard, NOT that a non-participant who FORGES a participant id is locked out (the
/// double does not provide that — a liar can derive the key). The real no-leak guarantee is delivered by the C5
/// roster-bound resolver (node-secret, root-seed-bound, non-party-id-derivable keys); production today registers the
/// fail-closed <see cref="NoDmConversationKeyProvider"/> and keeps DMs unexposed until C5.
/// </para>
/// </remarks>
public sealed class CommsDmConvergenceTests : IAsyncLifetime
{
    private readonly System.Collections.Generic.List<Replica> _replicas = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var r in _replicas) await r.DisposeAsync();
    }

    private const string Tenant = "team-7e57";

    // ── Two DM participants + a third (non-participant) team member, each a distinct keypair + member id. ────
    private sealed class Member
    {
        public required string PartyId { get; init; }
        public required IOperationSigner Signer { get; init; }
        public static Member New(string partyId) => new() { PartyId = partyId, Signer = new Ed25519Signer(KeyPair.Generate()) };
    }

    private static readonly Member Alice = Member.New("alice");
    private static readonly Member Bob = Member.New("bob");
    private static readonly Member Carol = Member.New("carol"); // a TEAM member, NOT a DM participant — the leak adversary.
    private static readonly IOperationVerifier Verifier = new Ed25519Verifier();

    // ── C4 — per-member root seeds (deterministic per member, signature-compatible with the C5 resolver). ────
    // NOTE: with the TEST-DOUBLE resolver this seed is NOT actually mixed into the key (the double derives the
    // keypair from the party id alone, which is exactly why it is insecure and test-only). It is passed for
    // signature-compatibility with the C5 roster-bound resolver, where the private key WILL be root-seed-bound.
    private static byte[] SeedFor(string partyId)
    {
        var seed = new byte[32];
        var src = System.Text.Encoding.UTF8.GetBytes("sunfish-dm-test-seed:" + partyId);
        System.Security.Cryptography.SHA256.HashData(src).AsSpan(0, 32).CopyTo(seed);
        return seed;
    }

    /// <summary>A DM key provider for a replica acting AS <paramref name="activeMember"/> (C4 seed-derived resolver).</summary>
    private static IDmConversationKeyProvider DmKeyProviderFor(Member activeMember) =>
        new DerivedDmConversationKeyProvider(
            new SeedDerivedParticipantDmKeyResolver(SeedFor(activeMember.PartyId), activeMember.PartyId));

    /// <summary>The deterministic DM id for the alice↔bob pair (both ends derive this identically — no coordination).</summary>
    private static readonly string AliceBobDmId = DmConversationId.Derive(Tenant, Alice.PartyId, Bob.PartyId);

    // ── A replica that owns BOTH a team-channel projection AND the alice↔bob DM projection. ─────────────────
    private sealed class Replica : IAsyncDisposable
    {
        public required string Name { get; init; }
        public required string Dir { get; init; }
        public required ServiceProvider Sp { get; init; }
        public required IDbContextFactory<NodeLocalCommsDbContext> Factory { get; init; }
        public required CommsCrdtProjection Team { get; init; }
        public required CommsCrdtProjection Dm { get; init; }

        public async ValueTask DisposeAsync()
        {
            await Team.DisposeAsync();
            await Dm.DisposeAsync();
            await Sp.DisposeAsync();
            try { if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// A replica acting AS <paramref name="activeMember"/>. Its DM projection is KEYED for that member over the
    /// alice↔bob conversation — so a PARTICIPANT (alice/bob) can seal/unseal, and a NON-PARTICIPANT (carol)
    /// derives no key and holds only ciphertext (the leak property). The team projection is always plaintext.
    /// </summary>
    private async Task<Replica> NewReplicaAsync(string name, Member activeMember)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"harborline-comms-dm-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContextFactory<NodeLocalCommsDbContext>(opt => opt.UseSqlite($"Data Source={Path.Combine(dir, "comms.db")};Pooling=False"));
        services.AddSingleton<ICrdtEngine, YDotNetCrdtEngine>();

        var sp = services.BuildServiceProvider();
        var factory = sp.GetRequiredService<IDbContextFactory<NodeLocalCommsDbContext>>();
        await using (var ctx = await factory.CreateDbContextAsync()) await ctx.Database.EnsureCreatedAsync();

        var engine = sp.GetRequiredService<ICrdtEngine>();
        var team = new CommsCrdtProjection(engine, factory, Verifier, NullLogger<CommsCrdtProjection>.Instance,
            conversationId: CommsConversation.TeamConversationId);
        // C4 — the DM projection is KEYED for this replica's active member over the alice↔bob participant pair.
        var dm = new CommsCrdtProjection(engine, factory, Verifier, NullLogger<CommsCrdtProjection>.Instance,
            rosterBinding: null,
            conversationId: AliceBobDmId,
            dmKeyProvider: DmKeyProviderFor(activeMember),
            dmParticipantA: Alice.PartyId,
            dmParticipantB: Bob.PartyId);

        var replica = new Replica { Name = name, Dir = dir, Sp = sp, Factory = factory, Team = team, Dm = dm };
        _replicas.Add(replica);
        return replica;
    }

    /// <summary>Append a PLAINTEXT (team-channel) message.</summary>
    private static async Task<MessageCrdtState> AppendAsync(
        CommsCrdtProjection conv, Member author, string body, DateTimeOffset at)
    {
        var message = await CommsMessageFactory.CreateSignedAsync(
            author.Signer, author.PartyId, Tenant, body, at, conversationId: conv.ConversationId);
        await conv.PersistLocalAsync(message, CancellationToken.None);
        conv.AppendLocal(message);
        return message;
    }

    /// <summary>
    /// Append a SEALED DM message (C4) on a participant's keyed DM projection — signs the plaintext, seals the
    /// body to the per-conversation key. <paramref name="author"/> must be a participant (alice/bob) so the
    /// projection can derive the key.
    /// </summary>
    private static async Task<MessageCrdtState> AppendSealedDmAsync(
        CommsCrdtProjection dm, Member author, string body, DateTimeOffset at)
    {
        Assert.True(dm.CanSealConversation, "the DM projection must be able to seal (the author is a participant)");
        var message = await dm.CreateSignedSealedLocalAsync(
            author.Signer, author.PartyId, Tenant, body, at, CancellationToken.None);
        await dm.PersistLocalAsync(message, CancellationToken.None);
        dm.AppendLocal(message);
        return message;
    }

    /// <summary>One direction of a sync round between two projections OF THE SAME conversation.</summary>
    private static async Task SyncAsync(CommsCrdtProjection src, CommsCrdtProjection dst)
    {
        var delta = await src.EncodeOutboundDeltaAsync(src.ConversationId, dst.VectorClock, CancellationToken.None);
        Assert.NotNull(delta);
        await dst.ApplyInboundDeltaAsync(src.ConversationId, 1, delta!.Value, CancellationToken.None);
        await dst.DrainPendingReconcilesAsync();
    }

    private static Task<System.Collections.Generic.IReadOnlyList<MessageCrdtState>> ReadAsync(CommsCrdtProjection c) =>
        c.ReadLogAsync(Tenant, CancellationToken.None);

    // ── THE CORE C2 PROOF: a DM round-trips A↔B on the deterministic dm: id, scoped from the team channel. ──

    [Fact(DisplayName = "C4: a SEALED DM round-trips A↔B (both directions), unseals for participants, attributed")]
    public async Task Dm_RoundTrips_A_To_B_BothWays_Attributed()
    {
        var a = await NewReplicaAsync("A", Alice);
        var b = await NewReplicaAsync("B", Bob);

        // Both ends derived the SAME dm: id with no coordination (the projection's conversation id).
        Assert.Equal(AliceBobDmId, a.Dm.ConversationId);
        Assert.Equal(AliceBobDmId, b.Dm.ConversationId);

        // A → B: Alice sends a SEALED DM; the CIPHERTEXT converges to B's DM thread; B (a participant) UNSEALS it,
        // attributed to Alice. The at-rest body on B is CIPHERTEXT (sealed); the unsealed read returns plaintext.
        var t0 = DateTimeOffset.UtcNow;
        await AppendSealedDmAsync(a.Dm, Alice, "Bob, just you and me", t0);
        await SyncAsync(a.Dm, b.Dm);

        var bDmAtRest = await ReadAsync(b.Dm);
        Assert.Single(bDmAtRest);
        Assert.True(DmContentSeal.IsSealed(bDmAtRest[0].Body)); // CIPHERTEXT at rest (the seal).
        Assert.NotEqual("Bob, just you and me", bDmAtRest[0].Body);

        var bDm = await b.Dm.ReadUnsealedLogAsync(Tenant, CancellationToken.None); // participant unseal
        Assert.Single(bDm);
        Assert.Equal("Bob, just you and me", bDm[0].Body); // plaintext for the participant
        Assert.Equal(Alice.PartyId, bDm[0].AuthorPartyId);
        Assert.Equal(AliceBobDmId, bDm[0].ConversationId);
        // Authorship verifies over the UNSEALED plaintext (sign-then-encrypt, DR-3).
        Assert.True(CommsMessageFactory.VerifySealedAuthorship(bDmAtRest[0], bDm[0].Body, Verifier));

        // B → A: Bob replies (sealed); it converges to A's DM thread; A (a participant) unseals it, attributed to Bob.
        await AppendSealedDmAsync(b.Dm, Bob, "Got it, Alice", t0.AddSeconds(1));
        await SyncAsync(b.Dm, a.Dm);

        var aDm = await a.Dm.ReadUnsealedLogAsync(Tenant, CancellationToken.None);
        Assert.Equal(2, aDm.Count);
        Assert.Contains(aDm, m => m.Body == "Got it, Alice" && m.AuthorPartyId == Bob.PartyId);
    }

    [Fact(DisplayName = "C4: a sealed DM is SCOPED — its ciphertext does NOT bleed into the team channel")]
    public async Task Dm_Is_Scoped_From_Team_Channel()
    {
        var a = await NewReplicaAsync("A", Alice);
        var b = await NewReplicaAsync("B", Bob);

        var t0 = DateTimeOffset.UtcNow;
        await AppendAsync(a.Team, Alice, "team announcement", t0);
        await AppendSealedDmAsync(a.Dm, Alice, "private to bob", t0.AddSeconds(1));

        // Sync the DM stream A→B; the team stream is NOT synced in this round.
        await SyncAsync(a.Dm, b.Dm);

        // B's DM thread has ONLY the DM message (unsealed = the plaintext); B's team thread is empty.
        var bDm = await b.Dm.ReadUnsealedLogAsync(Tenant, CancellationToken.None);
        var bTeam = await ReadAsync(b.Team);
        Assert.Equal(new[] { "private to bob" }, bDm.Select(m => m.Body).ToArray());
        Assert.Empty(bTeam);

        // And A's own threads are scoped: the team message is NOT in A's DM thread.
        var aDm = await a.Dm.ReadUnsealedLogAsync(Tenant, CancellationToken.None);
        Assert.DoesNotContain(aDm, m => m.Body == "team announcement");
    }

    [Fact(DisplayName = "C4: a message stamped with a DIFFERENT dm: id is DROPPED by the conversation-scope guard")]
    public async Task Cross_Dm_Message_Is_Dropped_By_Scope_Guard()
    {
        var a = await NewReplicaAsync("A", Alice);
        var b = await NewReplicaAsync("B", Bob);

        // A appends a legit alice↔bob sealed DM, plus a message stamped for a DIFFERENT dm: id (alice↔carol)
        // pushed straight onto the alice↔bob DM stream — exactly what a mis-routed/buggy frame looks like.
        var legit = await AppendSealedDmAsync(a.Dm, Alice, "for bob", DateTimeOffset.UtcNow);
        var aliceCarolId = DmConversationId.Derive(Tenant, Alice.PartyId, Carol.PartyId);
        var misrouted = await CommsMessageFactory.CreateSignedAsync(
            Alice.Signer, Alice.PartyId, Tenant, "for carol (mis-stamped onto bob's stream)",
            DateTimeOffset.UtcNow, conversationId: aliceCarolId);
        a.Dm.AppendLocal(misrouted); // push onto the alice↔bob stream WITHOUT an EF write (the CRDT list sent to the peer)

        await SyncAsync(a.Dm, b.Dm);

        // The CRDT list converged (merge-everything)...
        Assert.Equal(2, b.Dm.Count);
        // ...but the EF read store dropped the cross-conversation message (the C1 conversation-scope guard) — B's
        // alice↔bob DM thread holds ONLY the legit message.
        var bDm = await ReadAsync(b.Dm);
        Assert.Single(bDm);
        Assert.Equal(legit.MessageId, bDm[0].MessageId);
        Assert.DoesNotContain(bDm, m => m.MessageId == misrouted.MessageId);
    }

    // ── THE C4 CONSTRUCTION TEST (NOT the confidentiality guarantee — see the sec-eng deep-review of PR #1325). ─
    //
    // SCOPE — what this PROVES and what it does NOT. This exercises the C4 seal CONSTRUCTION end-to-end with a
    // clearly-labeled TEST-DOUBLE resolver (SeedDerivedParticipantDmKeyResolver, test-only): a sealed DM body
    // converges to a third node that acts AS an HONEST non-participant (Carol, reporting "carol"); her projection
    // cannot derive the per-conversation key (the self-guard returns null), so she stores only opaque ciphertext,
    // while the two participants unseal the plaintext. That demonstrates the seal/unseal/AAD round-trip + the
    // routing/fail-closed behaviour.
    //
    // It does NOT — and must not be read to — assert the no-leak GUARANTEE in production. With the test-double
    // resolver, a non-participant who LIES about their party id (constructs the resolver AS "alice") derives the
    // identical key and decrypts the body (the reviewer reproduced exactly this). The protection here is an
    // IDENTITY self-guard, not a cryptographic barrier. The REAL no-leak test — a non-participant who FORGES a
    // participant's party id STILL cannot obtain the key, because DM private keys are node-secret/root-seed-bound
    // and NOT party-id-derivable — lands in C5 with the roster-bound resolver. Production today registers the
    // fail-closed NoDmConversationKeyProvider (arch-fence ProductionComposition_RegistersFailClosed_NoDmKeyProvider),
    // so DMs derive no key at all and are NOT exposed (dev-gate OFF) until C5.

    [Fact(DisplayName = "C4 construction: an HONEST non-participant (test-double) holds only ciphertext; participants unseal — round-trip, NOT the prod guarantee")]
    public async Task C4_Dm_HonestNonParticipant_Holds_Only_Ciphertext_Construction()
    {
        var a = await NewReplicaAsync("A", Alice);
        var b = await NewReplicaAsync("B", Bob);
        // Carol's node holds a projection for the SAME alice↔bob dm: id but acts AS Carol (HONESTLY "carol") — so
        // her test-double key provider's self-guard returns null and she cannot derive the alice↔bob key. Models
        // "Carol's node received the DM delta (no C5 routing yet) and — being honest about who she is — cannot read
        // it." (A dishonest Carol claiming "alice" WOULD read it with this test-double — the C5 resolver closes
        // that; see the scope note above.)
        var carol = await NewReplicaAsync("Carol", Carol);

        const string secret = "secret for bob only";
        await AppendSealedDmAsync(a.Dm, Alice, secret, DateTimeOffset.UtcNow);

        // The delta reaches BOTH a participant (B) and the non-participant (Carol) — C5 routing has not filtered it.
        await SyncAsync(a.Dm, b.Dm);
        await SyncAsync(a.Dm, carol.Dm);

        // ── DATA-LAYER assertion: the honest non-participant holds NO PLAINTEXT of the DM. With C5's inbound
        //    fail-closed participant guard now LIVE, Carol's node DROPS the DM entirely (it does not even persist the
        //    opaque ciphertext, since she cannot derive the key) — stronger than the C4-only behaviour (which stored
        //    the opaque row). Either way the construction property holds: a non-participant gets NO plaintext. ─────
        var carolDm = await ReadAsync(carol.Dm);
        Assert.DoesNotContain(carolDm, m => m.Body == secret);          // no plaintext at rest (whether dropped or opaque).
        foreach (var m in carolDm) Assert.True(DmContentSeal.IsSealed(m.Body)); // any stored row is the sealed envelope.

        // Carol's NODE (honest) cannot derive the per-conversation key — her unsealed read recovers NO plaintext.
        var carolUnsealed = await carol.Dm.ReadUnsealedLogAsync(Tenant, CancellationToken.None);
        Assert.DoesNotContain(carolUnsealed, m => m.Body == secret);
        Assert.False(carol.Dm.CanSealConversation); // Carol's (honest) projection cannot derive the alice↔bob key.

        // ── A and B (PARTICIPANTS) read the plaintext fine — the construction round-trips. ─────────────────────
        var bUnsealed = await b.Dm.ReadUnsealedLogAsync(Tenant, CancellationToken.None);
        Assert.Single(bUnsealed);
        Assert.Equal(secret, bUnsealed[0].Body); // Bob (a participant) reads the plaintext.
        var aUnsealed = await a.Dm.ReadUnsealedLogAsync(Tenant, CancellationToken.None);
        Assert.Equal(secret, aUnsealed[0].Body); // Alice (the author, a participant) reads the plaintext.

        // The descriptor records the intended privacy; C4 builds the CONTENT seal, C5 makes the key non-forgeable.
        var descriptor = CommsConversationDescriptor.DirectMessage(Tenant, Alice.PartyId, Bob.PartyId);
        Assert.False(descriptor.IsParticipant(Carol.PartyId));
    }

    // ── THE C5 REAL FORGED-ID NO-LEAK TEST (the confidentiality GUARANTEE, with the ROSTER-BOUND resolver). ────
    //
    // This is the test the C4 construction test (above) deliberately is NOT: it uses the PRODUCTION roster-bound
    // resolver (RosterDmKeyResolver — each member's DM PRIVATE key is HKDF(that member's NODE root secret, teamId),
    // node-secret, NOT party-id-derivable; a DM peer's DM PUBLIC key comes from the shared roster). The adversary is
    // a TEAM MEMBER (Carol) who is NOT a DM participant and who is given EVERY advantage: A's + B's party ids, the
    // dm: hash, the ciphertext (on the same sync plane), AND she LIES — she builds her resolver claiming to be
    // "alice". She STILL cannot derive K_dm, because the ECDH needs alice's NODE-SECRET DM private key (a function
    // of alice's root seed, which Carol does not have), not alice's party id. The forged id yields a DIFFERENT,
    // useless key — the real no-leak guarantee (DR-5). Contrast the C4 stand-in, where the forged id yielded the
    // IDENTICAL key (the false positive the sec-eng deep-review caught).
    //
    // NOTE (the SECOND sec-eng deep-review, PR #1326): these resolver-level tests model the adversary FORGING an id
    // while holding only her own seed. They do NOT model the adversary WRITING/substituting a peer's DM PUBLIC key
    // into the roster's key-distribution channel (the C5-revision BLOCKER) — that is covered by the data-layer
    // substitution tests (RosterCrdtConvergenceTests.Substituted_Dm_Key_Synced_Delta_Is_Rejected_On_Victim,
    // RosterSyncRecordsTests.Substituted_Dm_Public_Key_Is_Rejected_Authentic_Survives) + the arch-fence
    // (CommsConversationScopeArchTests.Roster_Honors_Dm_Key_Only_From_Signed_Admission). The DM key is now bound into
    // the SIGNED admission, so BuildDmRoster's honestly-self-derived keys here are exactly what a real roster carries.
    // The DM keypair is scoped to this team id (a Guid). It is what NodeDmKeyDerivation salts on; the message
    // tenant + the seal AAD use the existing `Tenant` constant. (In production both are the active team; here the
    // harness uses a string tenant + a Guid team-scope, which is fine — the key derivation + the read filter are
    // each internally consistent: keys salt on DmTeamId+conversationId, reads filter on Tenant.)
    private const string DmTeamId = "0000c5c5-0000-0000-0000-000000000000";

    /// <summary>A real ROSTER-BOUND DM key provider for <paramref name="activeMember"/>, over a <paramref name="roster"/>
    /// that publishes each member's DM PUBLIC key (each derived from that member's own root seed). The active member's
    /// DM PRIVATE key is node-secret (derived from its root seed) — exactly the production construction.</summary>
    private static IDmConversationKeyProvider RosterBoundDmProviderFor(
        Member activeMember, Harborline.Api.LocalNodeHost.Enrollment.NodeTeamRoster roster) =>
        new DerivedDmConversationKeyProvider(
            new RosterDmKeyResolver(SeedFor(activeMember.PartyId), DmTeamId, activeMember.PartyId, roster));

    /// <summary>Build a shared roster that publishes the DM PUBLIC keys for the given members (each from its own seed).
    /// This is the by-association-validated roster DM-key map the production resolver reads.</summary>
    private static Harborline.Api.LocalNodeHost.Enrollment.NodeTeamRoster BuildDmRoster(params Member[] members)
    {
        var founder = members[0];
        var roster = new Harborline.Api.LocalNodeHost.Enrollment.NodeTeamRoster(
            Harborline.Api.Foundation.IdentityAtlas.MemberRoster.StableGenesis(
                Guid.Parse(DmTeamId), founder.PartyId, founder.Signer, Verifier));
        foreach (var m in members)
        {
            // Publish m's DM PUBLIC key (derived from m's OWN root seed) into the roster DM-key map. In production
            // this arrives on the synced roster record / enrollment wire; here we inject it directly (the public half
            // is public — what matters is the PRIVATE half stays node-secret, which RosterDmKeyResolver guarantees).
            var dmPub = Harborline.Api.LocalNodeHost.Enrollment.NodeDmKeyDerivation
                .DeriveDmPublicKey(SeedFor(m.PartyId), DmTeamId);
            roster.SetOwnDmPublicKey(m.PartyId, dmPub);
        }
        return roster;
    }

    [Fact(DisplayName = "C5 LEAK TEST (real): a non-participant TEAM MEMBER who FORGES a participant's id STILL cannot derive K_dm (node-secret keys)")]
    public void C5_Dm_ForgedId_NonParticipant_Cannot_Derive_Key()
    {
        // The shared roster publishes A's, B's, and Carol's DM PUBLIC keys (each from its own root seed). All three
        // are team members; Carol is NOT a DM participant of alice↔bob.
        var roster = BuildDmRoster(Alice, Bob, Carol);
        var dmId = DmConversationId.Derive(DmTeamId, Alice.PartyId, Bob.PartyId);

        // PARTICIPANTS derive the SAME key (alice's seed-private × bob's roster-public == bob's seed-private ×
        // alice's roster-public — the ECDH symmetry), with no coordination.
        var aliceProvider = RosterBoundDmProviderFor(Alice, roster);
        var bobProvider = RosterBoundDmProviderFor(Bob, roster);
        var aliceKey = aliceProvider.TryDeriveConversationKey(dmId, Alice.PartyId, Bob.PartyId);
        var bobKey = bobProvider.TryDeriveConversationKey(dmId, Alice.PartyId, Bob.PartyId);
        Assert.NotNull(aliceKey);
        Assert.NotNull(bobKey);
        Assert.True(aliceKey!.AsSpan().SequenceEqual(bobKey!), "both participants derive the IDENTICAL K_dm (ECDH symmetry)");

        // ── THE LEAK ASSERTION. Carol is a team member, NON-participant. She is HONEST first: her resolver reports
        //    "carol" → the self-guard returns null (she is not a participant). ───────────────────────────────────
        var carolHonest = RosterBoundDmProviderFor(Carol, roster);
        Assert.Null(carolHonest.TryDeriveConversationKey(dmId, Alice.PartyId, Bob.PartyId));

        // ── NOW THE ADVERSARY LIES: Carol builds her resolver claiming to BE "alice" (the forged party id), with
        //    EVERY advantage — A's + B's party ids, the dm: hash, the roster. She runs the SAME derivation a real
        //    alice would. She STILL cannot get the right key: her resolver's "alice" private key is CAROL's
        //    node-secret seed-derived key (SeedFor(Carol) — she has no other), NOT alice's. The ECDH against bob's
        //    roster-public yields a DIFFERENT, useless key. ─────────────────────────────────────────────────────
        var carolForgingAlice = new DerivedDmConversationKeyProvider(
            new RosterDmKeyResolver(SeedFor(Carol.PartyId), DmTeamId, Alice.PartyId /* FORGED id */, roster));
        var forgedKey = carolForgingAlice.TryDeriveConversationKey(dmId, Alice.PartyId, Bob.PartyId);
        // The forged-id derivation MAY return a (wrong) key — what matters is it is NOT the participants' key.
        if (forgedKey is not null)
        {
            Assert.False(forgedKey.AsSpan().SequenceEqual(aliceKey),
                "the FORGED-id key MUST differ from the real participants' K_dm (Carol used HER seed-private, not alice's)");
        }

        // And concretely: a body sealed by a participant is UNREADABLE with Carol's forged-id key. THIS is the leak
        // guarantee — Carol, the same-plane team member who knows both ids + the hash + lies about who she is, holds
        // only ciphertext she cannot open.
        const string secret = "for bob only — Carol must never read this";
        var sealedBody = DmContentSeal.Seal(secret, aliceKey, dmId, DmTeamId, Alice.PartyId, "m-leak");
        if (forgedKey is not null)
        {
            Assert.False(
                DmContentSeal.TryUnseal(sealedBody, forgedKey, dmId, DmTeamId, Alice.PartyId, "m-leak", out _),
                "Carol's forged-id key MUST NOT unseal the participants' DM (the real no-leak guarantee)");
        }
        // The participants' key DOES unseal it (sanity — the construction still works for the real participants).
        Assert.True(
            DmContentSeal.TryUnseal(sealedBody, bobKey, dmId, DmTeamId, Alice.PartyId, "m-leak", out var got));
        Assert.Equal(secret, got);

        System.Security.Cryptography.CryptographicOperations.ZeroMemory(aliceKey);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(bobKey);
        if (forgedKey is not null) System.Security.Cryptography.CryptographicOperations.ZeroMemory(forgedKey);
    }

    [Fact(DisplayName = "C5 LEAK TEST (real, full data-layer): a non-participant node holds ONLY ciphertext for an A↔B DM — even forging an id")]
    public async Task C5_Dm_NonParticipant_Node_Holds_Only_Ciphertext_RosterBound()
    {
        // The full 3-node data-layer leak test with the ROSTER-BOUND resolver: A↔B DM converges to a THIRD node
        // (Carol's) — and even running the forged-id derivation, Carol's node holds only ciphertext it cannot open.
        var roster = BuildDmRoster(Alice, Bob, Carol);
        var dmId = DmConversationId.Derive(DmTeamId, Alice.PartyId, Bob.PartyId);

        // A's + B's nodes are real participants (roster-bound providers). Carol's node FORGES "alice" — the worst case.
        var a = await NewRosterBoundReplicaAsync("A", Alice, Alice.PartyId, roster, dmId);
        var b = await NewRosterBoundReplicaAsync("B", Bob, Bob.PartyId, roster, dmId);
        var carolForging = await NewRosterBoundReplicaAsync("CarolForgingAlice", Carol, Alice.PartyId /* FORGED */, roster, dmId);

        const string secret = "C5: for bob only";
        await AppendSealedDmAsync(a.Dm, Alice, secret, DateTimeOffset.UtcNow);

        // The delta reaches B (a participant) AND Carol's node (no routing in this projection-level harness).
        await SyncAsync(a.Dm, b.Dm);
        await SyncAsync(a.Dm, carolForging.Dm);

        // Bob (a real participant) reads the plaintext. (Reads filter on the message TENANT = `Tenant`; the DM key
        // is scoped to DmTeamId via conversationId — each internally consistent.)
        var bUnsealed = await b.Dm.ReadUnsealedLogAsync(Tenant, CancellationToken.None);
        Assert.Single(bUnsealed);
        Assert.Equal(secret, bUnsealed[0].Body);

        // Carol's node — even FORGING "alice" — holds ONLY ciphertext at rest AND cannot unseal it (her forged-id
        // ECDH used HER seed-private, not alice's, so the key is wrong). THIS is the real leak guarantee.
        var carolAtRest = await ReadAsync(carolForging.Dm);
        // (The inbound fail-closed guard may also drop it — either way Carol never holds the plaintext. If present, it
        // is opaque ciphertext.)
        foreach (var m in carolAtRest)
        {
            Assert.True(DmContentSeal.IsSealed(m.Body));
            Assert.DoesNotContain(secret, m.Body, StringComparison.Ordinal);
        }
        var carolUnsealed = await carolForging.Dm.ReadUnsealedLogAsync(Tenant, CancellationToken.None);
        Assert.DoesNotContain(carolUnsealed, m => m.Body == secret);
    }

    /// <summary>A replica whose DM projection is keyed with the ROSTER-BOUND resolver for <paramref name="activeMember"/>,
    /// acting AS <paramref name="resolverPartyId"/> (= the real id for a participant, or a FORGED id for the adversary).</summary>
    private async Task<Replica> NewRosterBoundReplicaAsync(
        string name, Member activeMember, string resolverPartyId,
        Harborline.Api.LocalNodeHost.Enrollment.NodeTeamRoster roster, string dmId)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"harborline-comms-dm-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContextFactory<NodeLocalCommsDbContext>(opt => opt.UseSqlite($"Data Source={Path.Combine(dir, "comms.db")};Pooling=False"));
        services.AddSingleton<ICrdtEngine, YDotNetCrdtEngine>();
        var sp = services.BuildServiceProvider();
        var factory = sp.GetRequiredService<IDbContextFactory<NodeLocalCommsDbContext>>();
        await using (var ctx = await factory.CreateDbContextAsync()) await ctx.Database.EnsureCreatedAsync();
        var engine = sp.GetRequiredService<ICrdtEngine>();
        var team = new CommsCrdtProjection(engine, factory, Verifier, NullLogger<CommsCrdtProjection>.Instance,
            conversationId: CommsConversation.TeamConversationId);
        // The DM projection uses the ROSTER-BOUND resolver — the active member's DM private key derives from its OWN
        // seed (SeedFor(activeMember)); resolverPartyId is the id it claims (forged for the adversary).
        var dmProvider = new DerivedDmConversationKeyProvider(
            new RosterDmKeyResolver(SeedFor(activeMember.PartyId), DmTeamId, resolverPartyId, roster));
        var dm = new CommsCrdtProjection(engine, factory, Verifier, NullLogger<CommsCrdtProjection>.Instance,
            rosterBinding: null, conversationId: dmId,
            dmKeyProvider: dmProvider, dmParticipantA: Alice.PartyId, dmParticipantB: Bob.PartyId);
        var replica = new Replica { Name = name, Dir = dir, Sp = sp, Factory = factory, Team = team, Dm = dm };
        _replicas.Add(replica);
        return replica;
    }

    // ── bug-1332 — the JOINER cross-team DM test (the case the constant-DmTeamId tests above CANNOT catch). ─────
    //
    // The bug: the production RosterDmKeyResolver was PINNED at construction to the node's BOOT-GENESIS team id and
    // never refreshed when a JOINER switched its active team to the admitter's team on enrollment. So a joiner (B)
    // SEALED DMs with HKDF(B-root, B_genesisTeamId) while publishing — and the admitter (A) recording — B's
    // JOINED-team DM PUBLIC key HKDF(B-root, joinedTeamId). The two halves of the X25519 ECDH never agreed → neither
    // participant could decrypt the other's DM (both got "[unable to decrypt]"). NOT a leak (too restrictive), but
    // it made the exposed cross-team DM feature non-functional for joiners.
    //
    // The fix: the resolver follows the active team — it subscribes to IActiveTeamAccessor.ActiveChanged and
    // re-derives the active member's DM private key for the now-active (joined) team, mirroring the LocalNodeWorker
    // daemon-rebind. After the switch B's private key is HKDF(B-root, joinedTeamId), matching the public key it
    // published → the ECDH agrees → both decrypt.
    //
    // REFUTE-VERIFY: this test asserts BOTH directions decrypt across the join. Run against the OLD boot-pinned
    // resolver (no IActiveTeamAccessor, or one that never re-keys) it FAILS (B's key stays on its genesis team); with
    // the fix it PASSES. The constant-DmTeamId tests above never switch a team, so they stay green either way — which
    // is exactly why they could not catch this.

    /// <summary>A switchable fake active-team accessor: tests flip the active team and fire ActiveChanged, exactly as
    /// the join orchestrator's SetActiveAsync does — so a subscribed RosterDmKeyResolver re-keys to the joined team.</summary>
    private sealed class SwitchableActiveTeam : Harborline.Api.Kernel.Runtime.Teams.IActiveTeamAccessor
    {
        public Harborline.Api.Kernel.Runtime.Teams.TeamContext? Active { get; private set; }
        public event EventHandler<Harborline.Api.Kernel.Runtime.Teams.ActiveTeamChangedEventArgs>? ActiveChanged;

        public SwitchableActiveTeam(Guid initialTeamId) => Active = MakeContext(initialTeamId);

        public Task SetActiveAsync(Harborline.Api.Kernel.Runtime.Teams.TeamId teamId, CancellationToken ct)
        {
            SwitchTo(teamId.Value);
            return Task.CompletedTask;
        }

        /// <summary>Flip the active team to <paramref name="teamId"/> and FIRE ActiveChanged (the team-switch the
        /// joiner's SetActiveAsync triggers) — drives the resolver's DM-key rebind.</summary>
        public void SwitchTo(Guid teamId)
        {
            var prev = Active;
            Active = MakeContext(teamId);
            ActiveChanged?.Invoke(this, new Harborline.Api.Kernel.Runtime.Teams.ActiveTeamChangedEventArgs(prev, Active));
        }

        private static Harborline.Api.Kernel.Runtime.Teams.TeamContext MakeContext(Guid teamId) =>
            new(new Harborline.Api.Kernel.Runtime.Teams.TeamId(teamId), $"Team {teamId:D}",
                new ServiceCollection().BuildServiceProvider(), TimeProvider.System);
    }

    [Fact(DisplayName = "bug-1332: a JOINER's DM key FOLLOWS the active team — cross-team DM decrypts BOTH WAYS after the join")]
    public void Bug1332_Joiner_Dm_Key_Follows_Active_Team_Both_Decrypt_Across_Join()
    {
        // A is a native member of A's team (the joined/admitter team). B's OWN genesis team is a DIFFERENT team.
        var joinedTeamId = Guid.Parse("0000aaaa-0000-0000-0000-000000000000"); // A's team — B JOINS this.
        var bGenesisTeamId = Guid.Parse("0000bbbb-0000-0000-0000-000000000000"); // B's own genesis team (pre-join).
        var joinedTeamIdStr = joinedTeamId.ToString();
        var dmId = DmConversationId.Derive(joinedTeamIdStr, Alice.PartyId, Bob.PartyId);

        // The SHARED roster for the JOINED team (A's team) publishes the JOINED-TEAM DM PUBLIC keys — A's own and the
        // joined-team key B publishes on enrollment (HKDF(B-root, joinedTeamId)). This is what each party's resolver
        // reads as the PEER public key, and what a real synced roster carries post-admission.
        var roster = new Harborline.Api.LocalNodeHost.Enrollment.NodeTeamRoster(
            Harborline.Api.Foundation.IdentityAtlas.MemberRoster.StableGenesis(
                joinedTeamId, Alice.PartyId, Alice.Signer, Verifier));
        roster.SetOwnDmPublicKey(Alice.PartyId,
            Harborline.Api.LocalNodeHost.Enrollment.NodeDmKeyDerivation.DeriveDmPublicKey(SeedFor(Alice.PartyId), joinedTeamIdStr));
        roster.SetOwnDmPublicKey(Bob.PartyId,
            Harborline.Api.LocalNodeHost.Enrollment.NodeDmKeyDerivation.DeriveDmPublicKey(SeedFor(Bob.PartyId), joinedTeamIdStr));

        // A — a NATIVE member: its resolver is scoped to the joined team from the start (never switches). Its private
        // key is HKDF(A-root, joinedTeamId) — matching the public key the roster published for A.
        var aActiveTeam = new SwitchableActiveTeam(joinedTeamId);
        var aResolver = new RosterDmKeyResolver(SeedFor(Alice.PartyId), joinedTeamIdStr, Alice.PartyId, roster, aActiveTeam);
        var aProvider = new DerivedDmConversationKeyProvider(aResolver);

        // B — the JOINER: its resolver BOOTS on B's OWN genesis team (the boot floor), exactly as production wires it
        // (capturedGenesisTeamId). Pre-join, B would seal with HKDF(B-root, B_genesisTeamId) — the bug.
        var bActiveTeam = new SwitchableActiveTeam(bGenesisTeamId);
        var bResolver = new RosterDmKeyResolver(SeedFor(Bob.PartyId), bGenesisTeamId.ToString(), Bob.PartyId, roster, bActiveTeam);
        var bProvider = new DerivedDmConversationKeyProvider(bResolver);

        // SANITY (models the bug pre-fix-conditions): BEFORE the join, B's resolver is on its genesis team, so B's
        // derived key does NOT match A's — the cross-team ECDH disagrees (this is precisely the broken state).
        var aKeyBefore = aProvider.TryDeriveConversationKey(dmId, Alice.PartyId, Bob.PartyId);
        var bKeyBefore = bProvider.TryDeriveConversationKey(dmId, Alice.PartyId, Bob.PartyId);
        Assert.NotNull(aKeyBefore);
        Assert.NotNull(bKeyBefore);
        Assert.False(aKeyBefore!.AsSpan().SequenceEqual(bKeyBefore!),
            "PRE-JOIN: B (on its genesis team) and A (on the joined team) derive DIFFERENT keys — the broken state");

        // THE JOIN: B switches its active team to A's team (SetActiveAsync fires ActiveChanged) — exactly what
        // NodeEnrollmentJoinService does on enrollment. With the FIX, B's resolver re-keys to HKDF(B-root, joinedTeamId).
        bActiveTeam.SwitchTo(joinedTeamId);

        // AFTER THE JOIN: both participants derive the IDENTICAL key (ECDH symmetry over the SAME joined team) — the
        // fix. (Against the OLD boot-pinned resolver this assertion FAILS: B's key stays on its genesis team.)
        var aKey = aProvider.TryDeriveConversationKey(dmId, Alice.PartyId, Bob.PartyId);
        var bKey = bProvider.TryDeriveConversationKey(dmId, Alice.PartyId, Bob.PartyId);
        Assert.NotNull(aKey);
        Assert.NotNull(bKey);
        Assert.True(aKey!.AsSpan().SequenceEqual(bKey!),
            "POST-JOIN: B's DM key FOLLOWED the active team → both participants derive the IDENTICAL K_dm (bug-1332 fix)");

        // CONCRETE BOTH-WAYS DECRYPT — the real symptom the bug broke. A→B: A seals, B unseals to plaintext.
        const string aToB = "B, welcome to the team — only you and me";
        var sealedAToB = DmContentSeal.Seal(aToB, aKey, dmId, joinedTeamIdStr, Alice.PartyId, "m-a2b");
        Assert.True(
            DmContentSeal.TryUnseal(sealedAToB, bKey, dmId, joinedTeamIdStr, Alice.PartyId, "m-a2b", out var gotAToB),
            "A→B: the JOINER B decrypts A's DM after the join (was '[unable to decrypt]' pre-fix)");
        Assert.Equal(aToB, gotAToB);

        // B→A: B (the joiner) seals with its NOW-joined-team key; A unseals to plaintext.
        const string bToA = "Thanks — glad to be here";
        var sealedBToA = DmContentSeal.Seal(bToA, bKey, dmId, joinedTeamIdStr, Bob.PartyId, "m-b2a");
        Assert.True(
            DmContentSeal.TryUnseal(sealedBToA, aKey, dmId, joinedTeamIdStr, Bob.PartyId, "m-b2a", out var gotBToA),
            "B→A: A decrypts the JOINER B's DM after the join (was '[unable to decrypt]' pre-fix)");
        Assert.Equal(bToA, gotBToA);

        System.Security.Cryptography.CryptographicOperations.ZeroMemory(aKey);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(bKey);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(aKeyBefore);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(bKeyBefore);
        aResolver.Dispose();
        bResolver.Dispose();
    }

    [Fact(DisplayName = "bug-1332: a SINGLE-TEAM node (never a joiner) is UNAFFECTED — the DM key stays on its genesis team")]
    public void Bug1332_SingleTeam_Node_Unaffected_Key_Stays_On_Genesis()
    {
        // A node that never switches teams must behave EXACTLY as before — its DM key stays scoped to its genesis team
        // for the resolver's whole life (the rebind path is dormant). Two single-team members on the SAME genesis team
        // derive the IDENTICAL key with no switch (the ordinary same-team DM), proving the fix is inert when no join
        // happens.
        var teamId = Guid.Parse("0000c0c0-0000-0000-0000-000000000000");
        var teamIdStr = teamId.ToString();
        var dmId = DmConversationId.Derive(teamIdStr, Alice.PartyId, Bob.PartyId);

        var roster = new Harborline.Api.LocalNodeHost.Enrollment.NodeTeamRoster(
            Harborline.Api.Foundation.IdentityAtlas.MemberRoster.StableGenesis(teamId, Alice.PartyId, Alice.Signer, Verifier));
        roster.SetOwnDmPublicKey(Alice.PartyId,
            Harborline.Api.LocalNodeHost.Enrollment.NodeDmKeyDerivation.DeriveDmPublicKey(SeedFor(Alice.PartyId), teamIdStr));
        roster.SetOwnDmPublicKey(Bob.PartyId,
            Harborline.Api.LocalNodeHost.Enrollment.NodeDmKeyDerivation.DeriveDmPublicKey(SeedFor(Bob.PartyId), teamIdStr));

        // Both pin a single team and NEVER switch (a SwitchableActiveTeam fixed at the genesis team; ActiveChanged
        // never fires). Equivalent to passing activeTeam: null — the boot/genesis-team floor governs.
        var aTeam = new SwitchableActiveTeam(teamId);
        var bTeam = new SwitchableActiveTeam(teamId);
        var aResolver = new RosterDmKeyResolver(SeedFor(Alice.PartyId), teamIdStr, Alice.PartyId, roster, aTeam);
        var bResolver = new RosterDmKeyResolver(SeedFor(Bob.PartyId), teamIdStr, Bob.PartyId, roster, bTeam);
        var aKey = new DerivedDmConversationKeyProvider(aResolver).TryDeriveConversationKey(dmId, Alice.PartyId, Bob.PartyId);
        var bKey = new DerivedDmConversationKeyProvider(bResolver).TryDeriveConversationKey(dmId, Alice.PartyId, Bob.PartyId);
        Assert.NotNull(aKey);
        Assert.NotNull(bKey);
        Assert.True(aKey!.AsSpan().SequenceEqual(bKey!),
            "single-team members on the SAME genesis team derive the IDENTICAL key (no switch — the fix is inert)");

        System.Security.Cryptography.CryptographicOperations.ZeroMemory(aKey);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(bKey);
        aResolver.Dispose();
        bResolver.Dispose();
    }

    [Fact(DisplayName = "C4: a TAMPERED sealed DM body fails to unseal (fail-closed) — never surfaces plaintext")]
    public async Task C4_Tampered_Sealed_Body_Fails_Closed()
    {
        var a = await NewReplicaAsync("A", Alice);
        var b = await NewReplicaAsync("B", Bob);

        var msg = await AppendSealedDmAsync(a.Dm, Alice, "integrity-protected", DateTimeOffset.UtcNow);

        // Tamper the ciphertext: flip a char inside the envelope. Unseal MUST fail (AEAD tag mismatch).
        var tamperedBody = msg.Body[..^2] + (msg.Body[^1] == 'A' ? "Bb" : "Aa");
        var key = new DerivedDmConversationKeyProvider(
            new SeedDerivedParticipantDmKeyResolver(SeedFor(Bob.PartyId), Bob.PartyId))
            .TryDeriveConversationKey(AliceBobDmId, Alice.PartyId, Bob.PartyId);
        Assert.NotNull(key);
        var ok = DmContentSeal.TryUnseal(
            tamperedBody, key!, AliceBobDmId, Tenant, Alice.PartyId, msg.MessageId, out var pt);
        Assert.False(ok);          // fail-closed on tamper
        Assert.Equal(string.Empty, pt);
    }

    [Fact(DisplayName = "C4: multiple-DM isolation — A↔C ciphertext is unreadable on the A↔B thread (separate keys)")]
    public async Task C4_Multiple_Dm_Isolation_Separate_Keys()
    {
        // Two separate DM conversations share no key: a body sealed for A↔B cannot be unsealed with the A↔C key,
        // and vice-versa (the conversationId is the HKDF salt → distinct per-conversation keys).
        var aliceForBob = new DerivedDmConversationKeyProvider(
            new SeedDerivedParticipantDmKeyResolver(SeedFor(Alice.PartyId), Alice.PartyId));
        var aliceBobKey = aliceForBob.TryDeriveConversationKey(AliceBobDmId, Alice.PartyId, Bob.PartyId);
        var aliceCarolId = DmConversationId.Derive(Tenant, Alice.PartyId, Carol.PartyId);
        var aliceCarolKey = aliceForBob.TryDeriveConversationKey(aliceCarolId, Alice.PartyId, Carol.PartyId);
        Assert.NotNull(aliceBobKey);
        Assert.NotNull(aliceCarolKey);
        Assert.False(aliceBobKey!.AsSpan().SequenceEqual(aliceCarolKey!)); // distinct per-conversation keys.

        const string forBob = "only for bob";
        var sealedForBob = DmContentSeal.Seal(forBob, aliceBobKey, AliceBobDmId, Tenant, Alice.PartyId, "m1");
        // Unsealing the A↔B ciphertext with the A↔C key fails (different key) — DM threads are isolated.
        Assert.False(DmContentSeal.TryUnseal(sealedForBob, aliceCarolKey, AliceBobDmId, Tenant, Alice.PartyId, "m1", out _));
        // The right key unseals it.
        Assert.True(DmContentSeal.TryUnseal(sealedForBob, aliceBobKey, AliceBobDmId, Tenant, Alice.PartyId, "m1", out var got));
        Assert.Equal(forBob, got);
    }
}
