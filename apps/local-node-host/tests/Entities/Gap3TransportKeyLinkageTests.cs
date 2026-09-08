using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Crdt;
using Harborline.Api.Kernel.Crdt.Backends;
using Harborline.Api.Kernel.Security.Crypto;
using Harborline.Api.Kernel.Security.Keys;
using Harborline.Api.Kernel.Sync.Handshake;
using Harborline.Api.Kernel.Sync.Identity;
using Harborline.Api.Kernel.Sync.Protocol;
using Harborline.Api.LocalNodeHost.Data.Roster;
using Harborline.Api.LocalNodeHost.Enrollment;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// THE GAP-#3 TRANSPORT-KEY LINKAGE PROOF. The flagged gap-#3 leak was: <see cref="NodeTeamRoster.AdoptSyncedRoster"/>
/// did NOT touch the transport-key map, so once the admission surface wired admitted PEERS' transport subkeys into
/// the trust gate, a synced REVOCATION dropped the member's principal-key ATTRIBUTION binding but LEFT its
/// TRANSPORT key in the trust set — so a revoked member kept TRANSPORT-trust (could still complete the sync
/// handshake) post-convergence. These tests prove the closed linkage on the LIVE convergence path
/// (<see cref="RosterCrdtProjection"/> + <see cref="MemberSetTrustPolicy"/>, mirroring
/// <see cref="RosterCrdtConvergenceTests"/>):
/// <list type="number">
///   <item><b>Admitted-member-can-sync:</b> after <see cref="NodeTeamRoster.AdmitPeer"/> wires the peer's
///     team-scoped transport subkey, <see cref="MemberSetTrustPolicy.IsTrusted"/> accepts that peer's HELLO.</item>
///   <item><b>THE BITE — revoked-member-transport-key-DROPPED-post-converge:</b> a signed revocation that syncs +
///     converges drops the revoked member's transport key, so <see cref="MemberSetTrustPolicy.IsTrusted"/> now
///     REJECTS the same HELLO. Paired with a direct stale-key injection proof that the production path cannot
///     re-trust a revoked party.</item>
/// </list>
/// </summary>
/// <remarks>
/// A member's team-scoped transport subkey = HKDF(that member's root PRIVATE key, teamId) via
/// <see cref="TeamScopedNodeIdentity.Derive"/> — material the ADMITTING node never holds, so the joiner supplies
/// its transport PUBLIC key over the admission channel (the route does exactly this). These tests derive the
/// peer's real team-scoped transport subkey the SAME way the production registrar + the handshake do, so the
/// trust decision under test is the real one.
/// </remarks>
public sealed class Gap3TransportKeyLinkageTests : IAsyncLifetime
{
    private readonly List<Replica> _replicas = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var r in _replicas) await r.DisposeAsync();
    }

    private static readonly Guid Team = Guid.Parse("7e57ffff-0000-0000-0000-000000000006");
    private static readonly string TeamIdString = Team.ToString("D");
    private static readonly IOperationVerifier Verifier = new Ed25519Verifier();
    // The transport-subkey derivation primitive — the SAME one the per-team registrar + TeamScopedNodeIdentity use.
    private static readonly IEd25519Signer TransportSigner = new Harborline.Api.Kernel.Security.Crypto.Ed25519Signer();
    private static readonly ITeamSubkeyDerivation SubkeyDerivation = new TeamSubkeyDerivation(TransportSigner);

    // A team member: its comms/principal identity (the roster binding) + its node ROOT identity (the source of its
    // team-scoped transport subkey). Distinct per member, like two distinct installs.
    private sealed record Member(string PartyId, KeyPair PrincipalKey, IOperationSigner PrincipalSigner, NodeIdentity Root)
    {
        public static Member New(string partyId)
        {
            var kp = KeyPair.Generate();
            // A distinct 32-byte root seed → a distinct root identity → a distinct team-scoped transport subkey.
            var rootSeed = new byte[32];
            System.Security.Cryptography.RandomNumberGenerator.Fill(rootSeed);
            var (rootPub, _) = TransportSigner.GenerateFromSeed(rootSeed);
            var nodeIdBytes = new byte[16];
            Buffer.BlockCopy(rootPub, 0, nodeIdBytes, 0, 16);
            var nodeId = Convert.ToHexString(nodeIdBytes).ToLowerInvariant();
            var root = new NodeIdentity(nodeId, rootPub, rootSeed);
            return new Member(partyId, kp, new Harborline.Api.Foundation.Crypto.Ed25519Signer(kp), root);
        }

        /// <summary>The member's TEAM-SCOPED transport identity (HKDF(root-private, teamId)) — what it presents in
        /// the sync HELLO and what the trust gate must trust/drop.</summary>
        public NodeIdentity TransportIdentity =>
            TeamScopedNodeIdentity.Derive(Root, TeamIdString, SubkeyDerivation);

        /// <summary>The raw transport PUBLIC key the joiner supplies over the admission channel.</summary>
        public byte[] TransportPublicKey => TransportIdentity.PublicKey;
    }

    private sealed class Replica : IAsyncDisposable
    {
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

    private async Task<Replica> NewReplicaAsync(string name, MemberRoster seedRoster)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"harborline-gap3-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
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
            sp.GetRequiredService<ICrdtEngine>(), factory, Verifier,
            NullLogger<RosterCrdtProjection>.Instance, nodeRoster);

        var replica = new Replica { Dir = dir, Sp = sp, Factory = factory, Projection = projection, NodeRoster = nodeRoster };
        _replicas.Add(replica);
        return replica;
    }

    private static MemberRoster GenesisFor(Member founder) =>
        MemberRoster.Genesis(Team, founder.PartyId, founder.PrincipalSigner, Verifier, DateTimeOffset.UtcNow, Guid.NewGuid());

    private static async Task SeedLocalAsync(Replica r)
    {
        foreach (var rec in r.NodeRoster.Current.EnumerateAdmissions())
            await r.Projection.PublishLocalAsync(RosterRecordCrdtState.FromAdmission(rec), CancellationToken.None);
    }

    private static async Task SyncAsync(Replica src, Replica dst)
    {
        var delta = await src.Projection.EncodeOutboundDeltaAsync(
            RosterCrdtProjection.DocumentId, dst.Projection.VectorClock, CancellationToken.None);
        Assert.NotNull(delta);
        await dst.Projection.ApplyInboundDeltaAsync(
            RosterCrdtProjection.DocumentId, 1, delta!.Value, CancellationToken.None);
        await dst.Projection.DrainPendingReconcilesAsync();
    }

    /// <summary>The trust gate the per-team registrar builds: {own subkey floor} ∪ {admitted-member transport keys
    /// from the live ITrustedMemberKeyProvider}. Reads the snapshot on each call (admit/revoke take effect live).</summary>
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

    // ── PROOF 1: an admitted member's transport key is trusted by MemberSetTrustPolicy (can sync). ───────────

    [Fact(DisplayName = "gap #3: an ADMITTED member's transport key is trusted by MemberSetTrustPolicy (can sync)")]
    public void Admitted_Member_Transport_Key_Is_Trusted()
    {
        var founder = Member.New("founder");
        var bob = Member.New("bob");

        var roster = new NodeTeamRoster(GenesisFor(founder));
        var policy = TrustPolicyFor(roster, founder.TransportPublicKey); // own-subkey floor = founder's transport key

        // Pre-admit: bob's HELLO is NOT trusted (not yet a member).
        Assert.False(policy.IsTrusted(HelloFor(bob.TransportIdentity)));

        // Admit bob via the gap-#3 wiring: sign the principal binding into the roster AND record bob's transport key.
        var withBob = roster.Current.Admit(
            founder.PartyId, founder.PrincipalSigner, bob.PartyId, bob.PrincipalKey.PrincipalId,
            PermissionCompositions.Member, Verifier, DateTimeOffset.UtcNow, Guid.NewGuid());
        roster.AdmitPeer(withBob, bob.PartyId, bob.TransportPublicKey);

        // bob's HELLO is now trusted — he can complete the sync handshake. The own-subkey floor still trusts founder.
        Assert.True(policy.IsTrusted(HelloFor(bob.TransportIdentity)));
        Assert.True(policy.IsTrusted(HelloFor(founder.TransportIdentity)));
        // A stranger is still rejected (fail-closed).
        Assert.False(policy.IsTrusted(HelloFor(Member.New("stranger").TransportIdentity)));
    }

    // ── PROOF 2 (THE BITE): a SYNCED revocation DROPS the transport key → handshake REJECTED post-converge. ──

    [Fact(DisplayName = "gap #3 BITE: a SYNCED revocation DROPS the revoked member's transport key → handshake REJECTED post-converge")]
    public async Task Synced_Revocation_Drops_Transport_Key_Handshake_Rejected_Post_Converge()
    {
        var founder = Member.New("founder");
        var bob = Member.New("bob");

        // A = the admin/founder node; B = a third converging node that learns of bob via sync.
        var a = await NewReplicaAsync("A", GenesisFor(founder));
        await SeedLocalAsync(a);
        // B joins the team (trust root = A's genesis) — mirrors a node that adopts the team genesis on admission.
        var b = await NewReplicaAsync("B", a.NodeRoster.Current);

        // A admits bob (gap-#3 wiring: principal sign + transport-key record), publishes the admission, syncs to B.
        var withBob = a.NodeRoster.Current.Admit(
            founder.PartyId, founder.PrincipalSigner, bob.PartyId, bob.PrincipalKey.PrincipalId,
            PermissionCompositions.Member, Verifier, DateTimeOffset.UtcNow, Guid.NewGuid());
        a.NodeRoster.AdmitPeer(withBob, bob.PartyId, bob.TransportPublicKey);
        await a.Projection.PublishLocalAsync(
            RosterRecordCrdtState.FromAdmission(withBob.EnumerateAdmissions().Single(x => x.PartyId == bob.PartyId)),
            CancellationToken.None);
        await SyncAsync(a, b);

        // On B, bob is a converged member; B ALSO wires bob's transport key (the redeem path on B's side, or — as
        // here — B learns bob is trusted-for-sync because A told B bob's transport key via the admission channel).
        // We model B trusting bob by recording bob's transport key on B once converged (the production wiring does
        // this when B participates in the admission; here we record it to set up the revocation-drop bite on B).
        b.NodeRoster.AdmitPeer(b.NodeRoster.Current, bob.PartyId, bob.TransportPublicKey);

        var policyB = TrustPolicyFor(b.NodeRoster, founder.TransportPublicKey);
        // PRE-revocation: bob's HELLO is trusted on B (he can sync).
        Assert.True(policyB.IsTrusted(HelloFor(bob.TransportIdentity)));

        // A signs a REVOCATION of bob and publishes it; it syncs + converges to B.
        var (afterRevoke, signedRevocation) = a.NodeRoster.Current.SignRevoke(
            founder.PartyId, founder.PrincipalSigner, bob.PartyId, Verifier, DateTimeOffset.UtcNow, Guid.NewGuid());
        a.NodeRoster.AdoptSyncedRoster(afterRevoke);
        await a.Projection.PublishLocalAsync(
            RosterRecordCrdtState.FromRevocation(signedRevocation), CancellationToken.None);
        await SyncAsync(a, b);

        // POST-convergence: bob is dropped from B's principal roster (attribution gone) AND — gap #3 — his
        // TRANSPORT key is pruned, so MemberSetTrustPolicy now REJECTS his HELLO. A revoked member can no longer
        // complete the sync handshake. (Pre-fix: AdoptSyncedRoster left the transport map untouched, so this would
        // STILL be True — the leak.)
        Assert.False(b.NodeRoster.Current.Contains(bob.PartyId));         // attribution binding gone
        Assert.Null(b.NodeRoster.ForgeProofBinding(bob.PartyId));
        Assert.False(policyB.IsTrusted(HelloFor(bob.TransportIdentity))); // THE BITE — transport-trust dropped
        // The own-subkey floor still trusts the operator — not bricked.
        Assert.True(policyB.IsTrusted(HelloFor(founder.TransportIdentity)));
    }

    // ── PROOF 3: even a stale supplied transport key cannot re-trust a revoked member. ──

    [Fact(DisplayName = "gap #3: AdoptSyncedRoster rejects a stale transport key supplied for a revoked member")]
    public void AdoptSyncedRoster_Rejects_Stale_Transport_Key_For_Revoked_Member()
    {
        var founder = Member.New("founder");
        var bob = Member.New("bob");

        var roster = new NodeTeamRoster(GenesisFor(founder));
        var policy = TrustPolicyFor(roster, founder.TransportPublicKey);

        // Admit bob (records his transport key).
        var withBob = roster.Current.Admit(
            founder.PartyId, founder.PrincipalSigner, bob.PartyId, bob.PrincipalKey.PrincipalId,
            PermissionCompositions.Member, Verifier, DateTimeOffset.UtcNow, Guid.NewGuid());
        roster.AdmitPeer(withBob, bob.PartyId, bob.TransportPublicKey);
        Assert.True(policy.IsTrusted(HelloFor(bob.TransportIdentity)));

        // Revoke bob, then deliberately supply his stale key to the production synced-roster entry point. The
        // live-member filter must refuse it rather than reconstruct transport trust from unvalidated dictionary data.
        var afterRevoke = roster.Current.Revoke(founder.PartyId, bob.PartyId);
        roster.AdoptSyncedRoster(
            afterRevoke,
            new Dictionary<string, byte[]> { [bob.PartyId] = bob.TransportPublicKey });

        // bob is gone from both principal attribution and transport trust; the stale supplied key cannot revive him.
        Assert.False(afterRevoke.Contains(bob.PartyId));                  // attribution gone...
        Assert.False(policy.IsTrusted(HelloFor(bob.TransportIdentity)));  // ...and transport trust stays closed
    }
}
