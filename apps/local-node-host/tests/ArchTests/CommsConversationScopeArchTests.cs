using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Kernel.Crdt;
using Harborline.Api.Kernel.Crdt.Backends;
using Harborline.Api.LocalNodeHost.Data.Comms;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>
/// C1 conversation-scope fence — the "make the wrong wiring impossible" guard (design §4: a conversation
/// whose id is the team channel MUST be plaintext + all-team; a <c>dm:</c>/<c>grp:</c>/<c>chan:</c>
/// conversation MUST — once those increments land — be SEALED + bounded-recipient). C1 ships ONLY the team
/// channel, so the LIVE assertions here pin the C1 facts (the team channel = the well-known plaintext,
/// all-team conversation) and the doctype vocabulary the DM increments attach to. The
/// <see cref="C4_C5_extension_point"/> documents — and structurally reserves — the assertions C4 (content
/// seal) + C5 (participant-scoped routing) will GROW this fence into.
/// </summary>
/// <remarks>
/// <para>
/// <b>What C1 can prove (and these assert):</b>
/// <list type="bullet">
///   <item>the team channel id is the bare well-known constant <c>"team"</c> (no prefix) — so the pre-C1
///     default route + the EF back-fill default are preserved exactly;</item>
///   <item>the team channel classifies as TEAM and NOT as a direct message — so the (later) DM seal/routing
///     policy never applies to the team channel, and the team policy never applies to a DM;</item>
///   <item>a team-channel message is PLAINTEXT end-to-end — the body stored in the read model + carried in
///     the CRDT state IS the literal author text (no seal), the C1 visibility fact;</item>
///   <item>the reserved DM/group/channel prefixes are distinct from each other and from the team id — so the
///     id-prefix policy classification is unambiguous.</item>
/// </list>
/// </para>
/// <para>
/// <b>The C4/C5 extension point (NOT yet enforced — documented seam).</b> When DM content encryption (C4)
/// lands, this fence GROWS an assertion that a <c>dm:</c> conversation's stored body is CIPHERTEXT (never the
/// plaintext author text) + has a non-empty bounded recipient set; when participant-scoped routing (C5)
/// lands, it grows an assertion that a <c>dm:</c> stream's recipient set is exactly the two participants and
/// the team channel's is "all team". Those assertions cannot be written in C1 (no seal field, no recipient
/// set exists yet) — the seam is named so the later increments extend THIS file rather than re-deriving the
/// invariant. See the design doc §4.2 + §6.
/// </para>
/// </remarks>
public sealed class CommsConversationScopeArchTests
{
    private static readonly IOperationVerifier Verifier = new Ed25519Verifier();

    // ── C1 FACT: the team channel is the bare well-known constant "team" (no prefix). ───────────────────────

    [Fact(DisplayName = "C1 fence: the team channel id is the bare well-known constant \"team\" (no prefix)")]
    public void TeamChannel_Is_TheWellKnownBareConstant()
    {
        Assert.Equal("team", CommsConversation.TeamConversationId);
        // The team channel uses NO kind prefix — it is the pre-C1 default route, preserved exactly.
        Assert.False(CommsConversation.TeamConversationId.StartsWith(CommsConversation.DirectMessagePrefix, StringComparison.Ordinal));
        Assert.False(CommsConversation.TeamConversationId.StartsWith(CommsConversation.GroupPrefix, StringComparison.Ordinal));
        Assert.False(CommsConversation.TeamConversationId.StartsWith(CommsConversation.ChannelPrefix, StringComparison.Ordinal));
    }

    // ── C1 FACT: the team channel classifies as TEAM and NOT as a direct message. ───────────────────────────

    [Fact(DisplayName = "C1 fence: the team channel is TEAM and NOT a direct message (the DM policy never applies to it)")]
    public void TeamChannel_ClassifiesAsTeam_NotDm()
    {
        Assert.True(CommsConversation.IsTeam(CommsConversation.TeamConversationId));
        Assert.False(CommsConversation.IsDirectMessage(CommsConversation.TeamConversationId));

        // A dm:-prefixed id classifies as a DM and NOT as the team channel — the inverse, so the team policy
        // (plaintext + all-team) never applies to a DM. (C1 has no LIVE dm: conversation; this proves the
        // classifier the C4/C5 fence will key its seal/routing assertions off.)
        const string dmId = "dm:abc123example";
        Assert.True(CommsConversation.IsDirectMessage(dmId));
        Assert.False(CommsConversation.IsTeam(dmId));
    }

    // ── C1 FACT: a team-channel message is PLAINTEXT end-to-end (the C1 visibility fact). ────────────────────

    [Fact(DisplayName = "C1 fence: a team-channel message is PLAINTEXT — the stored + synced body IS the literal author text")]
    public async Task TeamChannel_Message_Is_Plaintext_EndToEnd()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"harborline-comms-arch-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(dir);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContextFactory<NodeLocalCommsDbContext>(o =>
            o.UseSqlite($"Data Source={System.IO.Path.Combine(dir, "comms.db")}"));
        services.AddSingleton<ICrdtEngine, YDotNetCrdtEngine>();
        await using var sp = services.BuildServiceProvider();
        var factory = sp.GetRequiredService<IDbContextFactory<NodeLocalCommsDbContext>>();
        await using (var ctx = await factory.CreateDbContextAsync())
            await ctx.Database.EnsureCreatedAsync();

        await using var team = new CommsCrdtProjection(
            sp.GetRequiredService<ICrdtEngine>(), factory, Verifier, NullLogger<CommsCrdtProjection>.Instance);
        // The team-channel projection owns the well-known "team" conversation.
        Assert.Equal(CommsConversation.TeamConversationId, team.ConversationId);

        var kp = KeyPair.Generate();
        var signer = new Ed25519Signer(kp);
        const string plaintext = "team standup at 10 — PLAINTEXT, not sealed";
        var message = await CommsMessageFactory.CreateSignedAsync(
            signer, authorPartyId: "alice", tenantId: "team-arch", body: plaintext,
            authoredAt: DateTimeOffset.UtcNow, conversationId: CommsConversation.TeamConversationId);

        // The signable + CRDT state carry the conversation + the LITERAL body (no seal in C1).
        Assert.Equal(CommsConversation.TeamConversationId, message.ConversationId);
        Assert.Equal(plaintext, message.Body);

        // The durable read row stores the LITERAL plaintext body (not ciphertext) for the team channel.
        await team.PersistLocalAsync(message, CancellationToken.None);
        await using (var ctx = await factory.CreateDbContextAsync())
        {
            var row = await ctx.Set<NodeMessage>().AsNoTracking().SingleAsync();
            Assert.Equal(CommsConversation.TeamConversationId, row.ConversationId);
            Assert.Equal(plaintext, row.Body); // PLAINTEXT at rest in the read model (C1 visibility fact)
        }

        // And the body re-verifies (the conversation id is inside the signed payload — replay-into-another-
        // thread defence; a team message signed for "team" verifies as "team").
        Assert.True(CommsMessageFactory.VerifyAuthorship(message, Verifier));

        try { System.IO.Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
    }

    // ── C1 FACT: the reserved kind prefixes are distinct (unambiguous id-prefix policy classification). ──────

    [Fact(DisplayName = "C1 fence: the reserved DM/group/channel prefixes are distinct (unambiguous classification)")]
    public void Reserved_Kind_Prefixes_Are_Distinct()
    {
        var prefixes = new[]
        {
            CommsConversation.DirectMessagePrefix,
            CommsConversation.GroupPrefix,
            CommsConversation.ChannelPrefix,
        };
        Assert.Equal(prefixes.Length, prefixes.Distinct().Count()); // all distinct
        foreach (var p in prefixes)
        {
            Assert.False(string.IsNullOrWhiteSpace(p));
            Assert.NotEqual(CommsConversation.TeamConversationId, p);
        }
    }

    // ── C2 FENCE: the conversation DESCRIPTOR invariant (design §4.2). ──────────────────────────────────────
    //
    // C2 adds the descriptor ({conversationId, kind, participantPartyIds, visibilityScope}), so the fence now
    // asserts the C2 facts the descriptor encodes: a dm: conversation MUST be kind=DirectMessage + Participants
    // visibility + EXACTLY 2 participant party ids; the team channel MUST stay kind=Team + AllTeam + the
    // "all team" sentinel (empty participant set). These are the structural "make the wrong wiring impossible"
    // guards the DM increments build on. (The dm:-MUST-be-SEALED + participant-ONLY-routing assertions remain
    // the C4/C5 extension point below — C2 is plaintext-but-gated.)

    [Fact(DisplayName = "C2 fence: a dm: conversation descriptor is DirectMessage + Participants + EXACTLY 2 parties")]
    public void Dm_Descriptor_Is_DirectMessage_Participants_TwoParties()
    {
        var dm = CommsConversationDescriptor.DirectMessage("team-arch", "alice", "bob");
        Assert.Equal(CommsConversationKind.DirectMessage, dm.Kind);
        Assert.Equal(CommsVisibilityScope.Participants, dm.VisibilityScope);
        Assert.Equal(2, dm.ParticipantPartyIds.Count);
        Assert.True(CommsConversation.IsDirectMessage(dm.ConversationId));
        dm.Validate(); // the invariant holds (throws if malformed)

        // The DM policy NEVER applies to the team channel, and the team policy never to a DM (inverse classification).
        var team = CommsConversationDescriptor.Team();
        Assert.Equal(CommsConversationKind.Team, team.Kind);
        Assert.Equal(CommsVisibilityScope.AllTeam, team.VisibilityScope);
        Assert.Empty(team.ParticipantPartyIds); // the "all team" sentinel — the roster is resolved live.
        team.Validate();
    }

    [Fact(DisplayName = "C2 fence: a malformed dm: descriptor (wrong participant count / scope) is REJECTED by Validate")]
    public void Malformed_Dm_Descriptor_Is_Rejected()
    {
        // A dm: descriptor with ≠2 participants violates the 1:1 invariant.
        var oneParty = new CommsConversationDescriptor(
            "dm:x", CommsConversationKind.DirectMessage, new[] { "alice" }, CommsVisibilityScope.Participants);
        Assert.Throws<InvalidOperationException>(() => oneParty.Validate());

        // A dm: descriptor with AllTeam visibility violates the participant-scoped invariant (a DM must be
        // Participants — the structural seed the C5 routing fence depends on).
        var allTeamDm = new CommsConversationDescriptor(
            "dm:x", CommsConversationKind.DirectMessage, new[] { "alice", "bob" }, CommsVisibilityScope.AllTeam);
        Assert.Throws<InvalidOperationException>(() => allTeamDm.Validate());

        // A team descriptor with a non-empty participant set violates the all-team sentinel invariant.
        var teamWithParticipants = new CommsConversationDescriptor(
            CommsConversation.TeamConversationId, CommsConversationKind.Team, new[] { "alice" }, CommsVisibilityScope.AllTeam);
        Assert.Throws<InvalidOperationException>(() => teamWithParticipants.Validate());
    }

    // ── C4 FENCE (NOW LIVE): a dm: conversation's stored body MUST be CIPHERTEXT; the team stays plaintext. ───
    //
    // C4 grows the documented C4/C5 extension point into a LIVE structural assertion (the "make the wrong wiring
    // impossible" guard): a sealed DM body carries the DmContentSeal envelope prefix (ciphertext), NEVER the
    // literal author text; the team channel body is the literal plaintext (asserted above). This is the body-half
    // of the design §4.2 invariant — the CONSTRUCTION fence. The confidentiality GUARANTEE (a forged party id
    // cannot derive the key) is NOT proven by C4 — it lands with the C5 roster-bound resolver; the B1 fence below
    // asserts production is fail-closed (NoDmConversationKeyProvider) in the meantime. (The C5 routing half — a
    // dm: stream's recipient set is EXACTLY the two participants + the inbound fail-closed guard — remains the C5
    // extension point at the bottom.)

    [Fact(DisplayName = "C4 fence: a dm: body is SEALED CIPHERTEXT (the seal envelope) — never the literal plaintext")]
    public async Task Dm_Body_Is_Sealed_Ciphertext_Never_Plaintext()
    {
        // The C4 derived-ECDH key for the alice↔bob conversation (the seed-derived stand-in resolver, acting as
        // alice). Both participants compute the same key; a non-participant cannot.
        var teamId = "team-arch";
        var dmId = DmConversationId.Derive(teamId, "alice", "bob");
        var aliceSeed = new byte[32];
        for (var i = 0; i < aliceSeed.Length; i++) aliceSeed[i] = (byte)(i + 7);
        var keyProvider = new DerivedDmConversationKeyProvider(
            new SeedDerivedParticipantDmKeyResolver(aliceSeed, "alice"));
        var key = keyProvider.TryDeriveConversationKey(dmId, "alice", "bob");
        Assert.NotNull(key);

        var kp = KeyPair.Generate();
        var signer = new Ed25519Signer(kp);
        const string plaintext = "for bob's eyes only — MUST be sealed, not plaintext";
        var sealedMessage = await CommsMessageFactory.CreateSignedSealedAsync(
            signer, authorPartyId: "alice", tenantId: teamId, plaintextBody: plaintext,
            authoredAt: DateTimeOffset.UtcNow, conversationId: dmId, conversationKey: key!);

        // The STORED/WIRE body is the SEAL envelope (ciphertext), NEVER the literal plaintext — the C4 fact.
        Assert.True(DmContentSeal.IsSealed(sealedMessage.Body));
        Assert.NotEqual(plaintext, sealedMessage.Body);
        Assert.DoesNotContain(plaintext, sealedMessage.Body, StringComparison.Ordinal);
        Assert.StartsWith(DmContentSeal.EnvelopePrefix, sealedMessage.Body, StringComparison.Ordinal);
        // The conversation id classifies as a DM (so the seal policy applies) — the team policy never does.
        Assert.True(CommsConversation.IsDirectMessage(sealedMessage.ConversationId));

        // A participant unseals it back to the plaintext; the signed payload covered the plaintext (DR-3).
        Assert.True(DmContentSeal.TryUnseal(
            sealedMessage.Body, key!, dmId, teamId, "alice", sealedMessage.MessageId, out var recovered));
        Assert.Equal(plaintext, recovered);
        Assert.True(CommsMessageFactory.VerifySealedAuthorship(sealedMessage, recovered, new Ed25519Verifier()));
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(key!);
    }

    [Fact(DisplayName = "C4 construction: the self-guard returns null for a NON-PARTICIPANT who reports a non-participant id (NOT the leak guarantee)")]
    public void NonParticipant_SelfGuard_Returns_Null_For_Honest_NonParticipant()
    {
        // NOTE (sec-eng deep-review of PR #1325): this asserts the CONSTRUCTION's self-guard, NOT a confidentiality
        // GUARANTEE. With the test-double resolver, a non-participant who LIES about their party id (claims
        // "alice") CAN derive the key — the guard is an identity check, not a cryptographic barrier. The real
        // no-leak guarantee (a forged party id yields a useless key) is delivered by the C5 roster-bound resolver
        // and its real leak test. Here we only confirm the provider returns null when honestly told "not a
        // participant" (the routing/fail-closed behaviour the merge gate relies on).
        var teamId = "team-arch";
        var dmId = DmConversationId.Derive(teamId, "alice", "bob");
        var carolSeed = new byte[32];
        for (var i = 0; i < carolSeed.Length; i++) carolSeed[i] = (byte)(i + 13);
        // Carol's node, HONESTLY reporting "carol", is not a participant → the self-guard returns null.
        var carolProvider = new DerivedDmConversationKeyProvider(
            new SeedDerivedParticipantDmKeyResolver(carolSeed, "carol"));
        Assert.Null(carolProvider.TryDeriveConversationKey(dmId, "alice", "bob"));
    }

    // ── B1 / C5 STRUCTURAL FENCE (sec-eng deep-review of PR #1325, completed in C5): the DM key resolver must ──
    //    NEVER be PARTY-ID-DERIVABLE. C4 made AddNodeComms FAIL-CLOSED (NoDm) until a real roster-bound resolver
    //    existed; C5 supplies it (RosterDmKeyResolver — node-secret, derived from the install root, NOT the party
    //    id) and wires it via AddRosterBoundDmKeyProvider in the FULL host. The two posture fences below pin BOTH
    //    invariants: (1) AddNodeComms ALONE stays fail-closed (a minimal graph can derive NO DM key — so a future
    //    host that forgets to wire the real provider degrades to fail-closed, never to party-id-derivable); (2) the
    //    real wiring (AddNodeComms + AddRosterBoundDmKeyProvider) registers the roster-bound resolver; (3) the only
    //    IParticipantDmKeyResolver in the SHIPPED assembly is the node-secret RosterDmKeyResolver — a party-id-
    //    derivable resolver (the C4 stand-in) lives ONLY in this test assembly and can NEVER be the prod resolver.

    [Fact(DisplayName = "B1 fence: AddNodeComms ALONE (no root secret / roster) stays FAIL-CLOSED — NoDmConversationKeyProvider")]
    public async Task AddNodeComms_Alone_Is_FailClosed_NoDmKeyProvider()
    {
        // AddNodeComms by itself — a minimal graph with no install root secret + no active team + no roster — MUST
        // remain fail-closed: it registers the null-object NoDm provider so a host that does NOT wire the real
        // roster-bound provider can derive NO DM key (degrade-to-fail-closed, never to party-id-derivable).
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"harborline-comms-b1fence-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(dir);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContextFactory<NodeLocalCommsDbContext>(o =>
            o.UseSqlite($"Data Source={System.IO.Path.Combine(dir, "comms.db")}"));
        services.AddNodeComms(); // ← AddNodeComms alone (no AddRosterBoundDmKeyProvider).

        await using var sp = services.BuildServiceProvider();
        var provider = sp.GetRequiredService<IDmConversationKeyProvider>();

        Assert.IsType<NoDmConversationKeyProvider>(provider);
        var teamId = "team-arch";
        var dmId = DmConversationId.Derive(teamId, "alice", "bob");
        Assert.Null(provider.TryDeriveConversationKey(dmId, "alice", "bob"));
        Assert.Null(provider.TryDeriveConversationKey(dmId, "bob", "alice"));

        try { System.IO.Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact(DisplayName = "C5 fence: the FULL prod wiring (AddNodeComms + AddRosterBoundDmKeyProvider) registers the ROSTER-BOUND resolver (NOT NoDm, NOT party-id-derivable)")]
    public async Task FullProdWiring_RegistersRosterBound_NotNoDm_NotPartyIdDerivable()
    {
        // The shipped host (Program.cs) is AddNodeComms() THEN AddRosterBoundDmKeyProvider(rootSeed, teamId, party).
        // After the override the DM key provider DI sees MUST be the real derived-ECDH provider over the roster-bound
        // resolver — NOT the NoDm null-object (so DMs CAN be sealed for a participant) and NOT a party-id-derivable
        // resolver (so a non-participant who forges a party id derives a DIFFERENT, useless key).
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"harborline-comms-c5fence-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(dir);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContextFactory<NodeLocalCommsDbContext>(o =>
            o.UseSqlite($"Data Source={System.IO.Path.Combine(dir, "comms.db")}"));

        // A live roster is required (the resolver resolves a peer DM key from it). Seed a genesis roster + the
        // founder's own DM key, exactly as Program.cs does.
        var teamId = Guid.NewGuid();
        var teamIdStr = teamId.ToString("D");
        var founderKp = KeyPair.Generate();
        var founderSigner = new Ed25519Signer(founderKp);
        var roster = new Harborline.Api.LocalNodeHost.Enrollment.NodeTeamRoster(
            Harborline.Api.Foundation.IdentityAtlas.MemberRoster.StableGenesis(
                teamId, "alice", founderSigner, new Ed25519Verifier()));
        var rootSeed = new byte[32];
        for (var i = 0; i < rootSeed.Length; i++) rootSeed[i] = (byte)(i + 41);
        var aliceDmPub = Harborline.Api.LocalNodeHost.Enrollment.NodeDmKeyDerivation.DeriveDmPublicKey(rootSeed, teamIdStr);
        roster.SetOwnDmPublicKey("alice", aliceDmPub);
        services.AddSingleton(roster);

        services.AddNodeComms();
        services.AddRosterBoundDmKeyProvider(rootSeed, teamIdStr, "alice"); // ← the FULL prod override.

        await using var sp = services.BuildServiceProvider();
        var provider = sp.GetRequiredService<IDmConversationKeyProvider>();

        Assert.IsType<DerivedDmConversationKeyProvider>(provider);
        Assert.IsNotType<NoDmConversationKeyProvider>(provider);

        // Alice (the active member) + Bob: with Bob's DM key in the roster, alice can derive K_dm; without it (Bob
        // not yet enrolled), it is null (fail-closed) — proving the resolver reads the ROSTER, not a constant.
        var dmId = DmConversationId.Derive(teamIdStr, "alice", "bob");
        Assert.Null(provider.TryDeriveConversationKey(dmId, "alice", "bob")); // bob has no roster DM key yet.

        // Add Bob's roster DM key (a distinct root) → now alice derives a key. (This confirms the resolver depends
        // on the ROSTER-published public key, not a party id: with Bob's key present alice's seed-private × bob's
        // roster-public yields K_dm.) Inject via the DM-map seam directly — the resolver reads DmPublicKeyOf.
        var bobSeed = new byte[32];
        for (var i = 0; i < bobSeed.Length; i++) bobSeed[i] = (byte)(i + 97);
        var bobDmPub = Harborline.Api.LocalNodeHost.Enrollment.NodeDmKeyDerivation.DeriveDmPublicKey(bobSeed, teamIdStr);
        roster.SetOwnDmPublicKey("bob", bobDmPub); // (SetOwnDmPublicKey writes the party→DM-pubkey map entry.)
        var key = provider.TryDeriveConversationKey(dmId, "alice", "bob");
        Assert.NotNull(key);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(key!);

        try { System.IO.Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact(DisplayName = "C5 fence: the SHIPPED assembly's ONLY IParticipantDmKeyResolver is the node-secret RosterDmKeyResolver (NOT a party-id-derivable one)")]
    public void ProductionAssembly_DmKeyResolver_Is_NodeSecret_RosterBound_Only()
    {
        // C5 ships a resolver in the production assembly (RosterDmKeyResolver) — so the C4-era "assembly declares
        // ZERO resolvers" fence is replaced by: the assembly's resolver(s) are EXACTLY {RosterDmKeyResolver}, and a
        // party-id-derivable resolver (the C4 stand-in SeedDerivedParticipantDmKeyResolver) is NOT in the shipped
        // assembly. The roster-bound resolver derives the active member's DM private key from the NODE ROOT SECRET
        // (not the party id), so a forged party id yields a different, useless key — the real no-leak guarantee.
        var productionAssembly = typeof(NodeCommsComposition).Assembly;
        Assert.Equal("Harborline.Api.LocalNodeHost", productionAssembly.GetName().Name);

        var resolverImpls = productionAssembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false }
                        && typeof(IParticipantDmKeyResolver).IsAssignableFrom(t))
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        // The ONLY shipped resolver is the node-secret roster-bound one.
        Assert.Equal(new[] { nameof(RosterDmKeyResolver) }, resolverImpls);

        // And the party-id-derivable stand-in is NOT in the shipped assembly (it lives ONLY in this test assembly).
        Assert.DoesNotContain(nameof(SeedDerivedParticipantDmKeyResolver), resolverImpls);
        var testDouble = typeof(SeedDerivedParticipantDmKeyResolver);
        Assert.Equal("Harborline.Api.LocalNodeHost.Tests", testDouble.Assembly.GetName().Name);
    }

    // ── C5 ARCH-FENCE (DM key-substitution fix; sec-eng deep-review BLOCKER of PR #1326): the roster accepts a ──
    //    DM key ONLY from a SIGNATURE-VALIDATED admission authenticated to that party. A DM key not authenticated to
    //    its owner (a substituted/unsigned record) is DROPPED. This is the structural "the wrong wiring is
    //    impossible" guard for the DM key-distribution channel — the DM public key is now bound INTO the signed
    //    admission envelope (AdmissionRecord.AdmittedDmPublicKey), so a roster writer cannot substitute a peer's DM
    //    key without re-signing (which it cannot). MemberRoster.DmPublicKeyOf surfaces ONLY the signed key.

    [Fact(DisplayName = "C5 arch-fence: the roster honors a DM key ONLY from the SIGNED admission — a substituted/unsigned DM key is DROPPED")]
    public void Roster_Honors_Dm_Key_Only_From_Signed_Admission()
    {
        var teamId = Guid.NewGuid();
        var founderKp = KeyPair.Generate();
        var founderSigner = new Ed25519Signer(founderKp);
        var aliceKp = KeyPair.Generate();

        // A distinct, well-formed 32-byte X25519 DM key per party (a valid curve point).
        static string Dm(byte seed)
        {
            var bytes = new byte[Harborline.Api.Foundation.Crypto.PrincipalId.LengthInBytes];
            for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)(seed + i);
            return Harborline.Api.Foundation.Crypto.PrincipalId.FromBytes(bytes).ToBase64Url();
        }
        var aliceAuthenticDm = Dm(0x20);
        var attackerDm = Dm(0xA0);

        // The founder admits Alice with her DM key SIGNED into the admission.
        var roster = Harborline.Api.Foundation.IdentityAtlas.MemberRoster
            .Genesis(teamId, "founder", founderSigner, Verifier, DateTimeOffset.UtcNow, Guid.NewGuid(),
                founderDmPublicKey: Dm(0x01))
            .Admit("founder", founderSigner, "alice",
                aliceKp.PrincipalId,
                Harborline.Api.Foundation.IdentityAtlas.Permissions.PermissionCompositions.Member,
                Verifier, DateTimeOffset.UtcNow, Guid.NewGuid(),
                newDmPublicKey: aliceAuthenticDm);

        // FENCE 1 — the roster surfaces ONLY the SIGNED DM key for Alice (the authority is the signed admission).
        var signedAliceDm = roster.DmPublicKeyOf("alice");
        Assert.NotNull(signedAliceDm);
        Assert.Equal(aliceAuthenticDm,
            Harborline.Api.Foundation.Crypto.PrincipalId.FromBytes(signedAliceDm!).ToBase64Url());

        // FENCE 2 — a record whose CARRIED DM key is SUBSTITUTED (away from the signed value) is DROPPED on rebuild,
        // so the substituted key NEVER reaches a live member. Take Alice's real synced record, rewrite the signed DM
        // field to the attacker's key WITHOUT re-signing (the attacker lacks the founder's key) → the signature no
        // longer validates → the record is dropped → Alice is NOT a live member of the rebuilt roster from that
        // record alone.
        var aliceRec = roster.EnumerateAdmissions().Single(r => r.PartyId == "alice");
        var substituted = aliceRec with
        {
            Admission = aliceRec.Admission with { DmPublicKey = attackerDm },
            DmPublicKey = Harborline.Api.Foundation.Crypto.PrincipalId.FromBase64Url(attackerDm).AsSpan().ToArray(),
        };
        var genesisRec = roster.EnumerateAdmissions().Single(r => r.Admission.IsGenesis);
        var rebuiltFromSubstitutedOnly = Harborline.Api.Foundation.IdentityAtlas.MemberRoster.FromSyncedRecords(
            new[] { genesisRec, substituted },
            System.Array.Empty<Harborline.Api.Foundation.IdentityAtlas.MemberRevocationRecord>(),
            Verifier);
        Assert.False(rebuiltFromSubstitutedOnly.Contains("alice")); // the substituted-DM-key record is DROPPED.
        Assert.Null(rebuiltFromSubstitutedOnly.DmPublicKeyOf("alice"));

        // FENCE 3 — with BOTH Alice's authentic record AND the substituted record present, the authentic key wins
        // (the substituted record is dropped; Alice is live via her real record carrying the signed key).
        var rebuiltWithBoth = Harborline.Api.Foundation.IdentityAtlas.MemberRoster.FromSyncedRecords(
            new[] { genesisRec, aliceRec, substituted },
            System.Array.Empty<Harborline.Api.Foundation.IdentityAtlas.MemberRevocationRecord>(),
            Verifier);
        Assert.True(rebuiltWithBoth.Contains("alice"));
        Assert.Equal(aliceAuthenticDm,
            Harborline.Api.Foundation.Crypto.PrincipalId.FromBytes(rebuiltWithBoth.DmPublicKeyOf("alice")!).ToBase64Url());

        // FENCE 4 (C5 round-2) — a same-party DM-key override by an AUTHORIZED ADMITTER is structurally rejected.
        // FENCE 2's substituted record fails the SIGNATURE check; this is the harder case the re-review surfaced —
        // an admin (legitimately holding members:admit) signs a FRESH, fully-VALID admission of an existing
        // participant carrying a different DM key. The signature/authority/no-escalation gates all PASS, so only
        // FIRST-WRITE-WINS keeps the binding immutable: the earliest genesis-rooted admission for a party fixes its
        // DM key, and a later same-party admission — even one signed by an authorized admitter — CANNOT overwrite it.
        // This is the structural "admit-authority ≠ key-binding-authority" guard.
        var adminKp = KeyPair.Generate();
        var adminSigner = new Ed25519Signer(adminKp);
        var rosterWithAdmin = roster.Admit(
            "founder", founderSigner, "admin", adminKp.PrincipalId,
            Harborline.Api.Foundation.IdentityAtlas.Permissions.PermissionCompositions.Admin,
            Verifier, DateTimeOffset.UnixEpoch.AddSeconds(1000), System.Guid.NewGuid(), newDmPublicKey: Dm(0x70));
        // The admin re-admits Alice (LATER issuance, fresh nonce) with the attacker's DM key — a genuine signature.
        var adminReAdmitAlice = Harborline.Api.Foundation.IdentityAtlas.RosterSigning.SignAdmission(
            signer: adminSigner, teamId: teamId, admittedPartyId: "alice",
            admittedPublicKey: aliceKp.PrincipalId, admittedByPartyId: "admin",
            isGenesis: false, issuedAt: DateTimeOffset.UnixEpoch.AddSeconds(2000), nonce: System.Guid.NewGuid(),
            admittedDmPublicKey: attackerDm);
        var adminOverrideRec = new Harborline.Api.Foundation.IdentityAtlas.MemberAdmissionRecord(
            teamId.ToString("D"), "alice", aliceKp.PrincipalId,
            adminReAdmitAlice, TransportPublicKey: null,
            DmPublicKey: Harborline.Api.Foundation.Crypto.PrincipalId.FromBase64Url(attackerDm).AsSpan().ToArray());
        var fence4Records = rosterWithAdmin.EnumerateAdmissions().Append(adminOverrideRec).ToArray();
        var rebuiltFence4 = Harborline.Api.Foundation.IdentityAtlas.MemberRoster.FromSyncedRecords(
            fence4Records,
            System.Array.Empty<Harborline.Api.Foundation.IdentityAtlas.MemberRevocationRecord>(),
            Verifier);
        Assert.True(rebuiltFence4.Contains("alice"));
        Assert.Equal(aliceAuthenticDm,
            Harborline.Api.Foundation.Crypto.PrincipalId.FromBytes(rebuiltFence4.DmPublicKeyOf("alice")!).ToBase64Url());
    }

    // ── C5 EXTENSION POINT (documented + reserved; NOT yet enforced — C5 adds participant-scoped routing). ────

    /// <summary>
    /// The seam C5 (participant-scoped sync routing) grows this fence into. C4 sealed the body (above); C5 adds
    /// the sync-plane half: a <c>dm:</c> stream's outbound recipient set is EXACTLY the two participants, and the
    /// inbound fail-closed guard drops a <c>dm:</c> frame the local member is not a participant of (defence in
    /// depth — even with C4's seal making a leaked frame unreadable, C5 stops the frame reaching a non-participant
    /// at all). The descriptor's <see cref="CommsConversationDescriptor.ParticipantPartyIds"/> +
    /// <see cref="CommsConversationDescriptor.IsParticipant"/> are the seam C5's routing/inbound-guard fence keys
    /// off. (See design §3.2 + §6.)
    /// </summary>
    [Fact(DisplayName = "C4 fence: C5 extension point is documented + reserved (participant-scoped routing lands here)")]
    public void C5_extension_point()
    {
        var dm = CommsConversationDescriptor.DirectMessage("team-arch", "alice", "bob");
        Assert.True(dm.IsParticipant("alice"));
        Assert.True(dm.IsParticipant("bob"));
        Assert.False(dm.IsParticipant("carol")); // the leak adversary C5's routing fence will keep the frame from.
        Assert.True(CommsConversation.IsDirectMessage(dm.ConversationId));
        Assert.False(CommsConversation.IsDirectMessage(CommsConversation.TeamConversationId));
    }
}
