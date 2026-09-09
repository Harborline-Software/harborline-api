using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Crdt;
using Harborline.Api.Kernel.Crdt.Backends;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Data.Roster;
using Harborline.Api.LocalNodeHost.Enrollment;
using Harborline.Api.LocalNodeHost.Tests.Authorization;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// Acceptance coverage for the ROSTER-SYNC doctype — the foundational production-wiring gap #1: the trust
/// roster propagates across nodes so admit/revoke records reach all team members. Proves the make-or-break
/// properties on the live doctype path (the CRDT list + delta-router merge, mirroring
/// <c>CommsCrdtConvergenceTests</c>):
/// <list type="number">
///   <item>an admission signed on node A SYNCS to node B → B's live <see cref="NodeTeamRoster"/> gains the
///     member (validated to genesis);</item>
///   <item><b>THE INTEGRITY PROOF — a FORGED roster record (signed by a non-admin / non-roster key) sent as a
///     delta is REJECTED — it does NOT add a member</b> (the trust-injection guard bites on the real merge
///     path, not just in a unit test);</item>
///   <item>a signed revocation SYNCS + drops the member on B (eventual-convergence — the offline window);</item>
///   <item>single-user still works (the local genesis self-admission seeds the roster; it syncs ON TOP).</item>
/// </list>
/// </summary>
/// <remarks>
/// <b>Two in-proc replicas, real YDotNet backend, NO networking.</b> Convergence is proven with two
/// in-process <see cref="RosterCrdtProjection"/> replicas (each over its own CRDT engine + its own SQLite
/// store + its own <see cref="NodeTeamRoster"/>) exchanging deltas directly — simulating the gossip daemon's
/// produce/consume round. The convergence assertions exercise the production <see cref="YDotNetCrdtEngine"/>.
/// </remarks>
public sealed class RosterCrdtConvergenceTests : IAsyncLifetime
{
    private readonly List<Replica> _replicas = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        // Dispose any restart "boot 2" reprojections FIRST (they share a prior replica's store/SP, so they must
        // release their CRDT document before the owning replica tears the SP down).
        foreach (var p in _bootReprojections) await p.DisposeAsync();
        foreach (var r in _replicas) await r.DisposeAsync();
    }

    private static readonly Guid Team = Guid.Parse("7e57eeee-0000-0000-0000-000000000005");
    private static readonly IOperationVerifier Verifier = new Ed25519Verifier();

    private sealed record Identity(string PartyId, KeyPair Key, IOperationSigner Signer)
    {
        public static Identity New(string partyId)
        {
            var kp = KeyPair.Generate();
            return new Identity(partyId, kp, new Ed25519Signer(kp));
        }
    }

    // ── A small in-proc replica: own CRDT engine, own SQLite store, own projection + live roster ─────────
    private sealed class Replica : IAsyncDisposable
    {
        public required string Name { get; init; }
        public required string Dir { get; init; }
        public required ServiceProvider Sp { get; init; }
        public required IDbContextFactory<NodeLocalRosterDbContext> Factory { get; init; }
        public required RosterCrdtProjection Projection { get; init; }
        public required NodeTeamRoster NodeRoster { get; init; }
        public NodeAdministratorAuthority? Administrators { get; init; }

        public async ValueTask DisposeAsync()
        {
            await Projection.DisposeAsync();
            await Sp.DisposeAsync();
            try { if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true); }
            catch { /* best-effort temp cleanup */ }
        }
    }

    /// <summary>
    /// Build the GENESIS-founder replica — seeded with a genesis self-admission roster for
    /// <paramref name="founder"/> (the single-office operator who founded the team).
    /// </summary>
    private async Task<Replica> NewReplicaAsync(string name, Identity founder)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"harborline-roster-crdt-{name}-{Guid.NewGuid():N}");
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

        var genesisRoster = MemberRoster.Genesis(
            Team, founder.PartyId, founder.Signer, Verifier, DateTimeOffset.UtcNow, Guid.NewGuid());
        var nodeRoster = new NodeTeamRoster(genesisRoster);

        var projection = new RosterCrdtProjection(TimeProvider.System,
            sp.GetRequiredService<ICrdtEngine>(),
            factory,
            Verifier,
            founder.Signer,
            NullLogger<RosterCrdtProjection>.Instance,
            nodeRoster);

        var replica = new Replica
        {
            Name = name, Dir = dir, Sp = sp, Factory = factory, Projection = projection, NodeRoster = nodeRoster,
        };
        _replicas.Add(replica);
        return replica;
    }

    /// <summary>
    /// Build a founder replica the way the PRODUCTION host boots (#1291 F1): the genesis is the deterministic,
    /// restart-stable <see cref="MemberRoster.StableGenesis"/> (NOT a random-nonce <see cref="MemberRoster.Genesis"/>).
    /// Use this for the restart-stability proof so the "boot 2" re-mint is byte-identical.
    /// </summary>
    private async Task<Replica> NewBootReplicaAsync(string name, Identity founder)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"harborline-roster-crdt-{name}-{Guid.NewGuid():N}");
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

        // PRODUCTION boot path: a deterministic, restart-stable genesis (the F1 fix).
        var genesisRoster = MemberRoster.StableGenesis(Team, founder.PartyId, founder.Signer, Verifier);
        var nodeRoster = new NodeTeamRoster(genesisRoster);
        var projection = new RosterCrdtProjection(TimeProvider.System,
            sp.GetRequiredService<ICrdtEngine>(), factory, Verifier, founder.Signer,
            NullLogger<RosterCrdtProjection>.Instance, nodeRoster);

        var replica = new Replica
        {
            Name = name, Dir = dir, Sp = sp, Factory = factory, Projection = projection, NodeRoster = nodeRoster,
        };
        _replicas.Add(replica);
        return replica;
    }

    /// <summary>
    /// Simulate a RESTART of <paramref name="prior"/> (#1291 F1): a FRESH projection + FRESH NodeTeamRoster over
    /// the SAME durable store and CRDT engine, with the genesis RE-MINTED from the same stable founder identity —
    /// exactly what Program.cs (genesis seed) + the bootstrap hosted service (hydrate + re-publish) do on boot 2.
    /// The new replica is NOT separately disposed-tracked beyond its projection (it shares the prior's store/SP,
    /// which the prior's DisposeAsync cleans up).
    /// </summary>
    private async Task<Replica> ReopenBootReplicaAsync(string name, Replica prior, Identity founder)
    {
        var genesisRoster = MemberRoster.StableGenesis(Team, founder.PartyId, founder.Signer, Verifier);
        var nodeRoster = new NodeTeamRoster(genesisRoster);
        var projection = new RosterCrdtProjection(TimeProvider.System,
            prior.Sp.GetRequiredService<ICrdtEngine>(), prior.Factory, Verifier, founder.Signer,
            NullLogger<RosterCrdtProjection>.Instance, nodeRoster);

        // Reuse the prior's Dir/Sp/Factory (the same on-disk store); register so its projection is disposed.
        var replica = new Replica
        {
            Name = name, Dir = prior.Dir, Sp = prior.Sp, Factory = prior.Factory,
            Projection = projection, NodeRoster = nodeRoster,
        };
        // Track ONLY the new projection for disposal (Sp/Dir are owned by `prior`); a dedicated disposer avoids a
        // double Sp/Directory teardown.
        _bootReprojections.Add(projection);
        return replica;
    }

    private readonly List<RosterCrdtProjection> _bootReprojections = new();

    /// <summary>
    /// Build a JOINER replica — a device admitted INTO the founder's team (it does NOT found its own competing
    /// team). Its <see cref="NodeTeamRoster"/> is seeded with the founder's genesis roster as the trust root (in
    /// production the joiner adopts the team genesis through the admission handshake — gap #3); the joiner does
    /// NOT publish its own genesis onto the shared doctype, so the team has exactly ONE genesis. The joiner's
    /// own (party, key) is admitted by the founder + synced as a regular admission record.
    /// </summary>
    private async Task<Replica> NewJoinerReplicaAsync(
        string name, MemberRoster teamGenesisRoster, IOperationSigner attestationSigner,
        bool withAdministratorAuthority = false)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"harborline-roster-crdt-{name}-{Guid.NewGuid():N}");
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

        // The joiner's trust root is the TEAM genesis (the founder's), not a self-genesis.
        var nodeRoster = new NodeTeamRoster(teamGenesisRoster);
        var administrators = withAdministratorAuthority
            ? new NodeAdministratorAuthority(factory, TimeProvider.System, TestAuthorization.AllowGate())
            : null;
        var projection = new RosterCrdtProjection(TimeProvider.System,
            sp.GetRequiredService<ICrdtEngine>(), factory, Verifier, attestationSigner,
            NullLogger<RosterCrdtProjection>.Instance, nodeRoster,
            administrators: administrators is null ? null : () => administrators);

        var replica = new Replica
        {
            Name = name, Dir = dir, Sp = sp, Factory = factory, Projection = projection, NodeRoster = nodeRoster,
            Administrators = administrators,
        };
        _replicas.Add(replica);
        return replica;
    }

    /// <summary>Publish every admission record currently in a replica's local roster onto its synced doctype
    /// (the bootstrap genesis-seed path).</summary>
    private static async Task SeedLocalAdmissionsAsync(Replica r)
    {
        foreach (var rec in r.NodeRoster.Current.EnumerateAdmissions())
        {
            await r.Projection.PublishLocalAsync(RosterRecordCrdtState.FromAdmission(rec), CancellationToken.None);
        }
    }

    /// <summary>Admit <paramref name="joiner"/> on the founder replica <paramref name="a"/> (signed by the
    /// founder), update A's live roster, and publish the admission record onto A's synced doctype.</summary>
    private static async Task AdmitAndPublishAsync(Replica a, Identity founder, Identity joiner, PermissionSet perms)
    {
        var withJoiner = a.NodeRoster.Current.Admit(
            founder.PartyId, founder.Signer, joiner.PartyId, joiner.Key.PrincipalId,
            perms, Verifier, DateTimeOffset.UtcNow, Guid.NewGuid());
        a.NodeRoster.AdoptSyncedRoster(withJoiner);
        var rec = withJoiner.EnumerateAdmissions().Single(x => x.PartyId == joiner.PartyId);
        await a.Projection.PublishLocalAsync(RosterRecordCrdtState.FromAdmission(rec), CancellationToken.None);
    }

    /// <summary>One direction of a sync round on the LIVE trigger: encode src's outbound delta against dst's
    /// clock, apply it to dst, then await the reconciles the projection's Changed→reconcile handler spawned.</summary>
    private static async Task SyncAsync(Replica src, Replica dst)
    {
        var delta = await src.Projection.EncodeOutboundDeltaAsync(
            RosterCrdtProjection.DocumentId, dst.Projection.VectorClock, CancellationToken.None);
        Assert.NotNull(delta);
        await dst.Projection.ApplyInboundDeltaAsync(
            RosterCrdtProjection.DocumentId, 1, delta!.Value, CancellationToken.None);
        await dst.Projection.DrainPendingReconcilesAsync();
    }

    // ── Test 1: admit on A → SYNCS to B → B's live roster gains the member, validated ───────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PermissionOnlySubstitutionOnInboundDeltaRefusesMember(bool elevate)
    {
        var founder = Identity.New("founder");
        var member = Identity.New("member");
        var source = await NewReplicaAsync("permission-source", founder);
        await SeedLocalAdmissionsAsync(source);
        var target = await NewJoinerReplicaAsync("permission-target", source.NodeRoster.Current, founder.Signer);
        var admitted = source.NodeRoster.Current.Admit("founder", founder.Signer, "member",
            member.Key.PrincipalId, PermissionCompositions.Member, Verifier,
            DateTimeOffset.UnixEpoch.AddDays(1), Guid.NewGuid());
        var genuine = RosterRecordCrdtState.FromAdmission(
            admitted.EnumerateAdmissions().Single(a => a.PartyId == "member"));
        var forged = genuine with
        {
            Permissions = (elevate ? PermissionCompositions.Owner : PermissionSet.Empty).Permissions.ToArray(),
        };
        Assert.Equal(genuine.SignatureB64Url, forged.SignatureB64Url);
        await source.Projection.PublishLocalAsync(forged, CancellationToken.None);
        await SyncAsync(source, target);
        Assert.False(target.NodeRoster.Current.Contains("member"));
        Assert.Null(target.NodeRoster.ForgeProofBinding("member"));
        Assert.True(target.NodeRoster.Current.ValidatesToGenesis(Verifier));

        // The exact genuine bytes are still admissible on a fresh replica.
        var cleanSource = await NewJoinerReplicaAsync("permission-clean", source.NodeRoster.Current, founder.Signer);
        await SeedLocalAdmissionsAsync(cleanSource);
        var cleanTarget = await NewJoinerReplicaAsync("permission-control", source.NodeRoster.Current, founder.Signer);
        await cleanSource.Projection.PublishLocalAsync(genuine, CancellationToken.None);
        await SyncAsync(cleanSource, cleanTarget);
        Assert.Equal(PermissionCompositions.Member, cleanTarget.NodeRoster.Current.PermissionsOf("member"));
    }

    [Fact(DisplayName = "roster-sync: admit on A → delta → B's live NodeTeamRoster gains the member (validated)")]
    public async Task Admit_On_A_Syncs_To_B_Live_Roster()
    {
        var founder = Identity.New("founder");
        var bob = Identity.New("bob"); // B's own device identity, admitted into A's team

        var a = await NewReplicaAsync("A", founder);
        await SeedLocalAdmissionsAsync(a);
        // B is a JOINER: its trust root is A's team genesis (adopted via the admission handshake — gap #3).
        var b = await NewJoinerReplicaAsync("B", a.NodeRoster.Current, founder.Signer);

        // A's admin admits bob (B's identity), updates A's local roster, and publishes the admission record.
        await AdmitAndPublishAsync(a, founder, bob, PermissionCompositions.Member);

        // The genesis + bob's admission records SYNC A → B.
        await SyncAsync(a, b);

        // B's LIVE roster now has founder + bob — validated to genesis (the forge-proof binding resolves bob's key).
        Assert.True(b.NodeRoster.Current.Contains("founder"));
        Assert.True(b.NodeRoster.Current.Contains("bob"));
        Assert.Equal(bob.Key.PrincipalId, b.NodeRoster.ForgeProofBinding("bob"));
        Assert.True(b.NodeRoster.Current.ValidatesToGenesis(Verifier));
    }

    // ── Test 2: THE INTEGRITY PROOF — a FORGED roster delta is REJECTED, no member injected ─────────────

    [Fact(DisplayName = "roster-sync INTEGRITY: a FORGED record (non-roster signer) synced as a delta is REJECTED")]
    public async Task Forged_Roster_Delta_Is_Rejected_No_Member_Injected()
    {
        var founder = Identity.New("founder");
        var a = await NewReplicaAsync("A", founder);
        await SeedLocalAdmissionsAsync(a);
        var b = await NewJoinerReplicaAsync("B", a.NodeRoster.Current, founder.Signer);

        // ATTACKER: a key that is NOT in the roster forges an admission of "mallory" and PUBLISHES it onto A's
        // synced doctype (as if A's node were compromised / a malicious peer injected the delta). The record is
        // structurally well-formed + cryptographically self-consistent (the attacker signed it), but the
        // attacker is NOT an in-roster admin.
        var attacker = Identity.New("attacker");
        var mallory = Identity.New("mallory");
        var forged = RosterSigning.SignAdmission(
            signer: attacker.Signer, teamId: Team, admittedPartyId: "mallory",
            admittedPublicKey: mallory.Key.PrincipalId, admittedByPartyId: "attacker",
            isGenesis: false, issuedAt: DateTimeOffset.UtcNow, nonce: Guid.NewGuid(),
            admittedPermissions: PermissionCompositions.Member);
        var forgedRecord = new MemberAdmissionRecord(
            Team.ToString("D"), "mallory", mallory.Key.PrincipalId, PermissionCompositions.Member, forged);
        await a.Projection.PublishLocalAsync(
            RosterRecordCrdtState.FromAdmission(forgedRecord), CancellationToken.None);

        // The forged record SYNCS to B as a delta...
        await SyncAsync(a, b);

        // ...and B's TRUST ANCHOR REJECTS it — mallory + attacker are NOT members on B's live roster. The
        // trust-injection guard BIT on the real merge path. A peer cannot inject a fake member.
        Assert.False(b.NodeRoster.Current.Contains("mallory"));
        Assert.False(b.NodeRoster.Current.Contains("attacker"));
        Assert.Null(b.NodeRoster.ForgeProofBinding("mallory"));
        // B still has its genuine genesis founder + a valid chain.
        Assert.True(b.NodeRoster.Current.Contains("founder"));
        Assert.True(b.NodeRoster.Current.ValidatesToGenesis(Verifier));
    }

    [Fact(DisplayName = "roster-sync INTEGRITY: a record with a TAMPERED signature synced as a delta is REJECTED")]
    public async Task Tampered_Signature_Roster_Delta_Is_Rejected()
    {
        var founder = Identity.New("founder");
        var bob = Identity.New("bob");
        var a = await NewReplicaAsync("A", founder);
        await SeedLocalAdmissionsAsync(a);
        var b = await NewJoinerReplicaAsync("B", a.NodeRoster.Current, founder.Signer);

        // A genuine admission of bob, but the SYNCED record's signature is corrupted in transit.
        var withBob = a.NodeRoster.Current.Admit(
            "founder", founder.Signer, "bob", bob.Key.PrincipalId,
            PermissionCompositions.Member, Verifier, DateTimeOffset.UtcNow, Guid.NewGuid());
        var bobRecord = withBob.EnumerateAdmissions().Single(x => x.PartyId == "bob");
        var wire = RosterRecordCrdtState.FromAdmission(bobRecord) with { SignatureB64Url = "AAAA-bogus" };
        await a.Projection.PublishLocalAsync(wire, CancellationToken.None);

        await SyncAsync(a, b);

        Assert.False(b.NodeRoster.Current.Contains("bob")); // dropped — signature no longer verifies
        Assert.True(b.NodeRoster.Current.Contains("founder"));
    }

    // ── Test 3: signed REVOCATION syncs + drops the member (eventual-convergence — the offline window) ──

    [Fact(DisplayName = "roster-sync REVOCATION: a signed revocation syncs A → B + drops the member on B")]
    public async Task Revocation_Syncs_And_Drops_Member_On_B()
    {
        var founder = Identity.New("founder");
        var bob = Identity.New("bob");
        var a = await NewReplicaAsync("A", founder);
        await SeedLocalAdmissionsAsync(a);
        var b = await NewJoinerReplicaAsync("B", a.NodeRoster.Current, founder.Signer);

        // Admit bob on A and sync — both nodes now honor bob.
        await AdmitAndPublishAsync(a, founder, bob, PermissionCompositions.Member);
        await SyncAsync(a, b);
        Assert.True(b.NodeRoster.Current.Contains("bob")); // pre-revocation: honored on B

        // Founder signs a REVOCATION of bob on A and publishes it.
        var (afterRevoke, signedRevocation) = a.NodeRoster.Current.SignRevoke(
            "founder", founder.Signer, "bob", Verifier, DateTimeOffset.UtcNow, Guid.NewGuid());
        a.NodeRoster.AdoptSyncedRoster(afterRevoke);
        Assert.False(a.NodeRoster.Current.Contains("bob")); // A drops bob immediately (the live op)
        await a.Projection.PublishLocalAsync(
            RosterRecordCrdtState.FromRevocation(signedRevocation), CancellationToken.None);

        // The revocation record SYNCS A → B → B's converged rebuild drops bob from live state + attribution.
        await SyncAsync(a, b);
        Assert.False(b.NodeRoster.Current.Contains("bob"));
        Assert.Null(b.NodeRoster.ForgeProofBinding("bob"));
        // Genesis-immutable: bob's admission stays in the chain, so the chain still validates.
        Assert.True(b.NodeRoster.Current.ValidatesToGenesis(Verifier));
    }

    [Fact(DisplayName = "roster-sync REVOCATION INTEGRITY: a revocation by a NON-ADMIN synced delta is REJECTED")]
    public async Task Forged_Revocation_By_NonAdmin_Synced_Delta_Is_Rejected()
    {
        var founder = Identity.New("founder");
        var bob = Identity.New("bob"); // plain member — no members:revoke
        var a = await NewReplicaAsync("A", founder);
        await SeedLocalAdmissionsAsync(a);
        var b = await NewJoinerReplicaAsync("B", a.NodeRoster.Current, founder.Signer);

        // Admit bob and sync.
        await AdmitAndPublishAsync(a, founder, bob, PermissionCompositions.Member);
        await SyncAsync(a, b);

        // bob (a plain member) forges a revocation of the FOUNDER with his own key + publishes it. He lacks
        // members:revoke → the rebuild must DROP it (no denial-of-trust injection).
        var forgedRevocation = RosterSigning.SignRevocation(
            signer: bob.Signer, teamId: Team, revokedPartyId: "founder", revokedByPartyId: "bob",
            issuedAt: DateTimeOffset.UtcNow, nonce: Guid.NewGuid());
        var forgedRecord = new MemberRevocationRecord(Team.ToString("D"), "founder", forgedRevocation);
        await a.Projection.PublishLocalAsync(
            RosterRecordCrdtState.FromRevocation(forgedRecord), CancellationToken.None);

        await SyncAsync(a, b);

        Assert.True(b.NodeRoster.Current.Contains("founder")); // the unauthorized revocation was DROPPED
        Assert.True(b.NodeRoster.Current.Contains("bob"));
    }

    [Fact(DisplayName = "roster-sync: a NOT-YET-SYNCED node honors the member until the revocation reaches it (offline window)")]
    public async Task Offline_Window_Member_Honored_On_B_Until_Revocation_Syncs()
    {
        var founder = Identity.New("founder");
        var bob = Identity.New("bob");
        var a = await NewReplicaAsync("A", founder);
        await SeedLocalAdmissionsAsync(a);
        var b = await NewJoinerReplicaAsync("B", a.NodeRoster.Current, founder.Signer);

        await AdmitAndPublishAsync(a, founder, bob, PermissionCompositions.Member);
        await SyncAsync(a, b);
        Assert.True(b.NodeRoster.Current.Contains("bob"));

        // A revokes bob and publishes the revocation — but B has NOT synced yet (the offline window).
        var (afterRevoke, signedRevocation) = a.NodeRoster.Current.SignRevoke(
            "founder", founder.Signer, "bob", Verifier, DateTimeOffset.UtcNow, Guid.NewGuid());
        a.NodeRoster.AdoptSyncedRoster(afterRevoke);
        await a.Projection.PublishLocalAsync(
            RosterRecordCrdtState.FromRevocation(signedRevocation), CancellationToken.None);

        // B still honors bob — no central authority; eventual-convergence, the window is open + NON-ZERO.
        Assert.True(b.NodeRoster.Current.Contains("bob"));

        // Once B syncs the revocation, the window closes — bob is dropped (convergence).
        await SyncAsync(a, b);
        Assert.False(b.NodeRoster.Current.Contains("bob"));
    }

    // ── Ticket 290: a CONVERGED revocation writes the administrator removal on the receiving node ───────

    [Fact(DisplayName = "ticket 290: a revocation converged from a peer writes the administrator removal on B")]
    public async Task Converged_Revocation_Writes_The_Administrator_Removal_On_B()
    {
        var founder = Identity.New("founder");
        var bob = Identity.New("bob");
        var a = await NewReplicaAsync("A", founder);
        await SeedLocalAdmissionsAsync(a);
        var b = await NewJoinerReplicaAsync("B", a.NodeRoster.Current, founder.Signer, withAdministratorAuthority: true);
        var administrators = b.Administrators!;
        var team = Team.ToString("D");

        await AdmitAndPublishAsync(a, founder, bob, PermissionCompositions.Member);
        await SyncAsync(a, b);
        Assert.True(b.NodeRoster.Current.Contains("bob"));

        // B's administrator-authority log holds BOTH parties — the state the boot projection reads. Seeded
        // after the admission sync so the fold that adopts the revocation is the first fold that sees them.
        await SeedAdministratorAsync(b, team, founder.PartyId);
        await SeedAdministratorAsync(b, team, bob.PartyId);
        Assert.Equal(2, (await administrators.UsableAsync()).Count);

        // A signs a REAL revocation of bob and publishes it; B learns of it only through the delta.
        var (afterRevoke, signedRevocation) = a.NodeRoster.Current.SignRevoke(
            "founder", founder.Signer, "bob", Verifier, DateTimeOffset.UtcNow, Guid.NewGuid());
        a.NodeRoster.AdoptSyncedRoster(afterRevoke);
        await a.Projection.PublishLocalAsync(
            RosterRecordCrdtState.FromRevocation(signedRevocation), CancellationToken.None);
        await SyncAsync(a, b);

        Assert.False(b.NodeRoster.Current.Contains("bob"));
        var usable = await administrators.UsableAsync();
        Assert.Equal(founder.PartyId, Assert.Single(usable).PartyId);

        await using var ctx = await b.Factory.CreateDbContextAsync();
        var removal = Assert.Single(await ctx.AdministratorAuthority
            .Where(record => record.PartyId == bob.PartyId
                && record.Event != AdministratorAuthorityEvent.Established)
            .ToListAsync());
        Assert.Equal(AdministratorAuthorityEvent.ReplacedByProjection, removal.Event);
        Assert.Equal(RosterCrdtProjection.RosterRevocationRemovalReason, removal.Reason);

        // The fold is idempotent: a second reconcile does not append a second removal.
        await b.Projection.DrainPendingReconcilesAsync();
        Assert.Single(await ctx.AdministratorAuthority
            .Where(record => record.PartyId == bob.PartyId
                && record.Event != AdministratorAuthorityEvent.Established)
            .ToListAsync());
    }

    [Fact(DisplayName = "ticket 290: a converged revocation whose successor has not arrived is PENDING, and completes when it does")]
    public async Task Converged_Revocation_Without_A_Successor_Is_Pending_Until_The_Successor_Arrives()
    {
        var founder = Identity.New("founder");
        var bob = Identity.New("bob");
        var a = await NewReplicaAsync("A", founder);
        await SeedLocalAdmissionsAsync(a);
        var b = await NewJoinerReplicaAsync("B", a.NodeRoster.Current, founder.Signer, withAdministratorAuthority: true);
        var administrators = b.Administrators!;
        var team = Team.ToString("D");

        await AdmitAndPublishAsync(a, founder, bob, PermissionCompositions.Member);
        await SyncAsync(a, b);

        // Only BOB is established on B: the successor's own establishment has not converged here yet. A's
        // guard admitted the revocation (A sees a successor); B's guard cannot yet.
        await SeedAdministratorAsync(b, team, bob.PartyId);

        var (afterRevoke, signedRevocation) = a.NodeRoster.Current.SignRevoke(
            "founder", founder.Signer, "bob", Verifier, DateTimeOffset.UtcNow, Guid.NewGuid());
        a.NodeRoster.AdoptSyncedRoster(afterRevoke);
        await a.Projection.PublishLocalAsync(
            RosterRecordCrdtState.FromRevocation(signedRevocation), CancellationToken.None);
        await SyncAsync(a, b);

        // Membership is not authority: the roster revocation IS applied, and the removal is PENDING.
        Assert.False(b.NodeRoster.Current.Contains("bob"));
        var pending = Assert.Single(b.Projection.PendingAdministratorRemovals);
        Assert.Equal(bob.PartyId, pending.PartyId);
        Assert.Equal(NodeAdministratorAuthority.LastUsableAdministratorCode, pending.Code);
        await using (var before = await b.Factory.CreateDbContextAsync())
        {
            Assert.Empty(await before.AdministratorAuthority
                .Where(record => record.Event != AdministratorAuthorityEvent.Established)
                .ToListAsync());
        }

        // The successor is established here, and the NEXT roster delta re-applies the pending removal.
        await SeedAdministratorAsync(b, team, founder.PartyId);
        await AdmitAndPublishAsync(a, founder, Identity.New("carol"), PermissionCompositions.Member);
        await SyncAsync(a, b);

        Assert.Empty(b.Projection.PendingAdministratorRemovals);
        var usable = await administrators.UsableAsync();
        Assert.Equal(founder.PartyId, Assert.Single(usable).PartyId);
        await using var ctx = await b.Factory.CreateDbContextAsync();
        var removal = Assert.Single(await ctx.AdministratorAuthority
            .Where(record => record.PartyId == bob.PartyId
                && record.Event != AdministratorAuthorityEvent.Established)
            .ToListAsync());
        Assert.Equal(AdministratorAuthorityEvent.ReplacedByProjection, removal.Event);
    }

    /// <summary>Seed an Established administrator row directly, the way an earlier boot's projection left it.</summary>
    private static async Task SeedAdministratorAsync(Replica r, string teamId, string partyId)
    {
        await using var context = await r.Factory.CreateDbContextAsync();
        var tip = await context.AdministratorAuthority.AsNoTracking()
            .OrderByDescending(record => record.Sequence).FirstOrDefaultAsync();
        var record = new AdministratorAuthorityRecord
        {
            Sequence = (tip?.Sequence ?? 0) + 1,
            TeamId = teamId,
            PartyId = partyId,
            Event = AdministratorAuthorityEvent.Established,
            Provenance = AdministratorProvenance.Recovery,
            MemberPublicKey = "cHVibGljLWtleQ",
            AdmissionSignature = "c2lnbmF0dXJl",
            AdmittedByPublicKey = "cHVibGljLWtleQ",
            AdmittedByPartyId = partyId,
            OccurredAtUtc = DateTimeOffset.UnixEpoch,
            Reason = "test-seed",
            PreviousHash = tip?.Hash ?? AdministratorAuthorityRecord.ZeroHash,
            Hash = string.Empty,
        };
        record.Hash = AdministratorAuthorityRecord.ComputeHash(record);
        context.AdministratorAuthority.Add(record);
        await context.SaveChangesAsync();
    }

    // ── Test 4: single-user node still works (local genesis seeds; nothing to sync yet) ─────────────────

    [Fact(DisplayName = "roster-sync: single-user node still works — local genesis seeds the synced roster")]
    public async Task Single_User_Node_Still_Works()
    {
        var founder = Identity.New("solo");
        var a = await NewReplicaAsync("solo", founder);
        await SeedLocalAdmissionsAsync(a);

        // The genesis self-admission is on the synced doctype (one record) and the live roster forge-proves the
        // operator's own identity — the node is not bricked by an empty roster.
        Assert.Equal(1, a.Projection.Count);
        Assert.True(a.NodeRoster.Current.Contains("solo"));
        Assert.Equal(founder.Key.PrincipalId, a.NodeRoster.ForgeProofBinding("solo"));

        // A re-publish (restart re-seed) is idempotent — no duplicate record.
        await SeedLocalAdmissionsAsync(a);
        Assert.Equal(1, a.Projection.Count);
    }

    // ── Test 5: cold-start hydration re-seeds the CRDT list from the durable store after a restart ───────

    [Fact(DisplayName = "roster-sync: durable records re-hydrate into the CRDT list on cold start (restart)")]
    public async Task Durable_Records_Rehydrate_On_Cold_Start()
    {
        var founder = Identity.New("founder");
        var alice = Identity.New("alice");
        var a = await NewReplicaAsync("A", founder);
        await SeedLocalAdmissionsAsync(a);

        var withAlice = a.NodeRoster.Current.Admit(
            "founder", founder.Signer, "alice", alice.Key.PrincipalId,
            PermissionCompositions.Member, Verifier, DateTimeOffset.UtcNow, Guid.NewGuid());
        await a.Projection.PublishLocalAsync(
            RosterRecordCrdtState.FromAdmission(withAlice.EnumerateAdmissions().Single(x => x.PartyId == "alice")),
            CancellationToken.None);
        Assert.Equal(2, a.Projection.Count); // genesis + alice persisted to the durable store

        // Simulate a restart: a fresh projection over the SAME store hydrates both records from disk.
        var freshProjection = new RosterCrdtProjection(TimeProvider.System,
            a.Sp.GetRequiredService<ICrdtEngine>(),
            a.Factory,
            Verifier,
            founder.Signer,
            NullLogger<RosterCrdtProjection>.Instance,
            nodeRoster: null);
        var hydrated = await freshProjection.HydrateFromStoreAsync(CancellationToken.None);
        Assert.Equal(2, hydrated);
        Assert.Equal(2, freshProjection.Count);
        await freshProjection.DisposeAsync();
    }

    // ── Test 6 (#1291 F1): a RESTART must NOT mint a second genesis — convergence survives a reboot ───────

    /// <summary>Count the GENESIS records in a projection's converged snapshot (admission records flagged
    /// genesis). The F1 defect manifests as this being &gt;1 after a restart.</summary>
    private static int CountGenesisRecords(RosterCrdtProjection projection) =>
        projection.Snapshot().Count(s => s.Kind == RosterRecordKind.Admission && s.IsGenesis);

    /// <summary>
    /// THE F1 RESTART-STABILITY PROOF. A node founds its team (stable genesis) + admits alice, then RESTARTS:
    /// the boot-2 path re-mints the genesis from the SAME stable identity and re-publishes it onto the hydrated
    /// store. With the deterministic <see cref="MemberRoster.StableGenesis"/> (the fix) the re-mint is
    /// byte-identical ⇒ deduped by RecordId ⇒ exactly ONE genesis survives ⇒ the converged roster still rebuilds
    /// non-empty AND adopts a synced member. Pre-fix (genesis with a fresh <c>Guid.NewGuid()</c> nonce per boot)
    /// the re-mint had a different RecordId ⇒ a SECOND genesis was appended ⇒ <see cref="MemberRoster.FromSyncedRecords"/>
    /// returns Empty ⇒ convergence permanently bricked. The sibling
    /// <see cref="OldRandomNonceGenesis_Reboot_Mints_A_Second_Genesis_Bite"/> exhibits that pre-fix failure
    /// directly, so this test's PASS is meaningful.
    /// </summary>
    [Fact(DisplayName = "roster-sync #1291 F1: a RESTART does NOT mint a second genesis; convergence still adopts a synced member")]
    public async Task Restart_Does_Not_Mint_Second_Genesis_Convergence_Survives()
    {
        var founder = Identity.New("founder");
        var alice = Identity.New("alice"); // a peer device admitted into the team — the synced member to adopt

        // ── BOOT 1: found the team with a STABLE genesis + seed it onto the synced doctype. ──
        var a = await NewBootReplicaAsync("A-boot1", founder);
        await SeedLocalAdmissionsAsync(a);
        Assert.Equal(1, CountGenesisRecords(a.Projection)); // one genesis after the first boot

        // Admit alice on A and publish — durable store now holds genesis + alice's admission.
        await AdmitAndPublishAsync(a, founder, alice, PermissionCompositions.Member);
        Assert.Equal(2, a.Projection.Count);

        // ── RESTART: a FRESH projection over the SAME durable store, with a FRESH NodeTeamRoster that re-mints
        // the genesis from the same stable founder identity (exactly what Program.cs + the bootstrap service do
        // on boot 2). Hydrate the boot-1 records, then re-publish the boot-2 genesis (the bootstrap seed path).
        var boot2 = await ReopenBootReplicaAsync("A-boot2", a, founder);

        // Cold-start hydration re-pushes boot-1's genesis + alice from disk...
        var hydrated = await boot2.Projection.HydrateFromStoreAsync(CancellationToken.None);
        Assert.Equal(2, hydrated);
        // ...then the bootstrap re-publishes boot-2's genesis self-admission (idempotent IFF the genesis is
        // restart-stable — this is the F1 path).
        await SeedLocalAdmissionsAsync(boot2);

        // THE BITE: exactly ONE genesis after the restart (not two). Pre-fix this was 2.
        Assert.Equal(1, CountGenesisRecords(boot2.Projection));
        Assert.Equal(2, boot2.Projection.Count); // genesis + alice — no spurious extra record

        // CONVERGENCE STILL WORKS after the restart: rebuild the live roster from the converged snapshot and
        // confirm it is non-empty AND adopts the synced member (alice), validated to genesis. Pre-fix the ≥2
        // genesis made this rebuild Empty ⇒ alice could never be adopted.
        boot2.Projection.RebuildLiveRoster(boot2.Projection.Snapshot());
        Assert.True(boot2.NodeRoster.Current.Contains("founder"));
        Assert.True(boot2.NodeRoster.Current.Contains("alice")); // the synced member is adopted post-restart
        Assert.Equal(alice.Key.PrincipalId, boot2.NodeRoster.ForgeProofBinding("alice"));
        Assert.True(boot2.NodeRoster.Current.ValidatesToGenesis(Verifier));

        // AND a PEER receiving this node's records sees exactly ONE genesis (no poison shipped downstream).
        var peer = await NewJoinerReplicaAsync("peer", boot2.NodeRoster.Current, founder.Signer);
        await SyncAsync(boot2, peer);
        Assert.Equal(1, CountGenesisRecords(peer.Projection));
        Assert.True(peer.NodeRoster.Current.Contains("alice"));
        Assert.True(peer.NodeRoster.Current.ValidatesToGenesis(Verifier));
    }

    /// <summary>
    /// THE BITE, made explicit. Reproduces the PRE-FIX failure mode in isolation: the OLD boot path
    /// (<see cref="MemberRoster.Genesis"/> with a fresh <c>Guid.NewGuid()</c> nonce on every boot) appends a
    /// SECOND genesis record on restart, and that ≥2-genesis snapshot rebuilds to an EMPTY roster (convergence
    /// bricked). This is the exact behaviour <see cref="MemberRoster.StableGenesis"/> eliminates — proving the
    /// F1 test above is testing something real, not a tautology.
    /// </summary>
    [Fact(DisplayName = "roster-sync #1291 F1 (bite): the OLD random-nonce genesis appends a 2nd genesis on reboot → rebuild bricks Empty")]
    public async Task OldRandomNonceGenesis_Reboot_Mints_A_Second_Genesis_Bite()
    {
        var founder = Identity.New("founder");

        // BOOT 1 with the OLD path: random-nonce genesis.
        var a = await NewReplicaAsync("old-boot1", founder); // NewReplicaAsync uses Genesis(Guid.NewGuid())
        await SeedLocalAdmissionsAsync(a);
        Assert.Equal(1, CountGenesisRecords(a.Projection));

        // RESTART with the OLD path: a fresh projection over the same store + a fresh NodeTeamRoster whose
        // genesis used ANOTHER random nonce (different RecordId).
        var boot2Genesis = MemberRoster.Genesis(
            Team, founder.PartyId, founder.Signer, Verifier, DateTimeOffset.UtcNow, Guid.NewGuid());
        var boot2Roster = new NodeTeamRoster(boot2Genesis);
        // Capture the boot-2 genesis SYNC RECORD up front (its random nonce ⇒ a different RecordId than boot-1).
        // We publish THIS record directly — the production bootstrap publishes _nodeRoster.Current's genesis,
        // and under the publish-races-the-hydration-reconcile window the boot-2 (un-rebuilt) genesis is what gets
        // shipped. Reading the captured record makes the pre-fix race-failure deterministic in the test.
        var boot2GenesisWire = RosterRecordCrdtState.FromAdmission(boot2Genesis.EnumerateAdmissions().Single());
        var boot2Projection = new RosterCrdtProjection(TimeProvider.System,
            a.Sp.GetRequiredService<ICrdtEngine>(), a.Factory, Verifier, founder.Signer,
            NullLogger<RosterCrdtProjection>.Instance, boot2Roster);

        await boot2Projection.HydrateFromStoreAsync(CancellationToken.None); // boot-1 genesis (nonce-1)
        // Bootstrap re-publish of boot-2 genesis (nonce-2) — DIFFERENT RecordId ⇒ NOT deduped ⇒ a 2nd genesis.
        await boot2Projection.PublishLocalAsync(boot2GenesisWire, CancellationToken.None);

        // The pre-fix defect: TWO genesis records.
        Assert.Equal(2, CountGenesisRecords(boot2Projection));

        // ...and that ≥2-genesis snapshot rebuilds to EMPTY — convergence bricked (FromSyncedRecords rejects).
        var rebuilt = MemberRoster.FromSyncedRecords(
            boot2Projection.Snapshot().Where(s => s.Kind == RosterRecordKind.Admission)
                .Select(s => s.ToAdmissionOrNull()).Where(x => x is not null).Select(x => x!),
            Array.Empty<MemberRevocationRecord>(),
            Verifier);
        Assert.Empty(rebuilt.Members); // the bricked state — exactly what StableGenesis prevents

        await boot2Projection.DisposeAsync();
    }

    /// <summary>StableGenesis is DETERMINISTIC — two boots of the same (team, founder, key) reconstruct the
    /// byte-identical genesis record (same RecordId, same nonce, same signature). The load-bearing F1 property.</summary>
    [Fact(DisplayName = "roster-sync #1291 F1: StableGenesis is deterministic across reboots (identical RecordId + signature)")]
    public void StableGenesis_Is_Deterministic_Across_Reboots()
    {
        var founder = Identity.New("founder");

        var g1 = MemberRoster.StableGenesis(Team, founder.PartyId, founder.Signer, Verifier);
        var g2 = MemberRoster.StableGenesis(Team, founder.PartyId, founder.Signer, Verifier);

        var r1 = RosterRecordCrdtState.FromAdmission(g1.EnumerateAdmissions().Single());
        var r2 = RosterRecordCrdtState.FromAdmission(g2.EnumerateAdmissions().Single());

        Assert.Equal(r1.RecordId, r2.RecordId);         // the dedup key — identical ⇒ a restart re-mint collapses
        Assert.Equal(r1.NonceGuid, r2.NonceGuid);       // deterministic nonce
        Assert.Equal(r1.SignatureB64Url, r2.SignatureB64Url); // byte-identical signed record
        Assert.True(g1.ValidatesToGenesis(Verifier));
        // A DIFFERENT founder/team derives a DIFFERENT nonce (no cross-install collision).
        var other = Identity.New("other-founder");
        var gOther = MemberRoster.StableGenesis(Team, other.PartyId, other.Signer, Verifier);
        var rOther = RosterRecordCrdtState.FromAdmission(gOther.EnumerateAdmissions().Single());
        Assert.NotEqual(r1.RecordId, rOther.RecordId);
    }

    // ── C5 DM KEY-SUBSTITUTION LEAK TEST (the data-layer integration of the sec-eng BLOCKER, PR #1326). ──────
    //
    // The full RosterCrdtProjection → RebuildLiveRoster → NodeTeamRoster.DmPublicKeyOf path: a legitimately-
    // admitted-but-malicious team member (Mallory) publishes a roster delta substituting a PARTICIPANT's (Alice's)
    // DM public key with HER OWN key. It SYNCS to a victim node (Bob). The victim's live roster MUST resolve
    // Alice's AUTHENTIC signed DM key, NOT Mallory's substitute — so Bob's RosterDmKeyResolver runs the ECDH
    // against Alice's real key and Mallory cannot derive K_dm. This is the test C5 was missing: the adversary
    // WRITING a peer's key into the roster (not merely forging an id with only her own seed).

    private static string DmKeyOf(string partyId)
    {
        // A distinct, deterministic 32-byte X25519 DM public key per party (what a member publishes / what an
        // attacker substitutes). Derived via the production NodeDmKeyDerivation so it is a valid curve point.
        var seed = new byte[32];
        System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("sunfish-dm-test-seed:" + partyId)).AsSpan(0, 32).CopyTo(seed);
        return PrincipalId.FromBytes(NodeDmKeyDerivation.DeriveDmPublicKey(seed, Team.ToString("D"))).ToBase64Url();
    }

    [Fact(DisplayName = "C5 LEAK TEST (data layer): a trusted member who SUBSTITUTES a participant's DM key in the synced roster is rejected on the victim node — authentic key resolves, substitute does NOT")]
    public async Task Substituted_Dm_Key_Synced_Delta_Is_Rejected_On_Victim()
    {
        var founder = Identity.New("founder");
        var alice = Identity.New("alice");     // a DM participant — the victim of the substitution.
        var mallory = Identity.New("mallory"); // a legit team member, NON-participant — the substitution attacker.
        var bob = Identity.New("bob");         // the other DM participant — the node we check resolves Alice's key.

        var aliceAuthenticDm = DmKeyOf("alice"); // Alice's REAL, signed-into-admission DM key.
        var malloryDm = DmKeyOf("mallory");      // Mallory's own DM key — what she substitutes for Alice's.

        // A founds the team and admits Alice (with her signed DM key) + Mallory + Bob.
        var a = await NewReplicaAsync("A", founder);
        await SeedLocalAdmissionsAsync(a);
        var aliceRoster = a.NodeRoster.Current.Admit(
            founder.PartyId, founder.Signer, "alice", alice.Key.PrincipalId,
            PermissionCompositions.Member, Verifier, DateTimeOffset.UtcNow, Guid.NewGuid(),
            newDmPublicKey: aliceAuthenticDm);
        var withMallory = aliceRoster.Admit(
            founder.PartyId, founder.Signer, "mallory", mallory.Key.PrincipalId,
            PermissionCompositions.Member, Verifier, DateTimeOffset.UtcNow, Guid.NewGuid(),
            newDmPublicKey: malloryDm);
        var withBob = withMallory.Admit(
            founder.PartyId, founder.Signer, "bob", bob.Key.PrincipalId,
            PermissionCompositions.Member, Verifier, DateTimeOffset.UtcNow, Guid.NewGuid(),
            newDmPublicKey: DmKeyOf("bob"));
        a.NodeRoster.AdoptSyncedRoster(withBob);
        foreach (var party in new[] { "alice", "mallory", "bob" })
        {
            var rec = withBob.EnumerateAdmissions().Single(x => x.PartyId == party);
            await a.Projection.PublishLocalAsync(RosterRecordCrdtState.FromAdmission(rec), CancellationToken.None);
        }

        // Bob (the victim) is a joiner whose trust root is A's team genesis.
        var bobNode = await NewJoinerReplicaAsync("Bob", a.NodeRoster.Current, founder.Signer);

        // ── THE ATTACK. Mallory emits a roster delta about ALICE with her OWN DM key substituted, signed by HER
        //    OWN key as admitter — the "I write a peer's key into the roster" injection. A fresh nonce ⇒ a distinct
        //    RecordId, so it coexists with Alice's real record. She publishes it onto A's doctype (modeling a
        //    compromised/malicious peer injecting the delta into the shared roster). ──────────────────────────────
        var malloryForged = RosterSigning.SignAdmission(
            signer: mallory.Signer, teamId: Team, admittedPartyId: "alice",
            admittedPublicKey: alice.Key.PrincipalId, admittedByPartyId: "mallory",
            isGenesis: false, issuedAt: DateTimeOffset.UtcNow, nonce: Guid.NewGuid(),
            admittedDmPublicKey: malloryDm, admittedPermissions: PermissionCompositions.Member);
        var malloryRecord = new MemberAdmissionRecord(
            Team.ToString("D"), "alice", alice.Key.PrincipalId, PermissionCompositions.Member,
            malloryForged, TransportPublicKey: null,
            DmPublicKey: PrincipalId.FromBase64Url(malloryDm).AsSpan().ToArray());
        await a.Projection.PublishLocalAsync(
            RosterRecordCrdtState.FromAdmission(malloryRecord), CancellationToken.None);

        // Everything (Alice's real record + Mallory's substitution + the rest) SYNCS to Bob's node.
        await SyncAsync(a, bobNode);

        // ── THE LEAK ASSERTION on the victim node. Alice is a live member (her authentic record validated); the
        //    DM key Bob's resolver will use for Alice is her AUTHENTIC signed key, NOT Mallory's substitute. The
        //    substitution record was dropped (its signature does not validate as a founder-signed admission of the
        //    real binding, and the harvest reads ONLY the signed key from the validated chain). ──────────────────
        Assert.True(bobNode.NodeRoster.Current.Contains("alice"));
        Assert.True(bobNode.NodeRoster.Current.Contains("mallory"));

        var resolvedAliceDm = bobNode.NodeRoster.DmPublicKeyOf("alice");
        Assert.NotNull(resolvedAliceDm);
        var resolvedB64 = PrincipalId.FromBytes(resolvedAliceDm!).ToBase64Url();
        Assert.Equal(aliceAuthenticDm, resolvedB64);  // Alice's AUTHENTIC key resolves on the victim node.
        Assert.NotEqual(malloryDm, resolvedB64);       // Mallory's SUBSTITUTED key is REJECTED — no MITM.
        Assert.True(bobNode.NodeRoster.Current.ValidatesToGenesis(Verifier));
    }

    // ── C5 round-2 DATA-LAYER LEAK TEST (sec-eng re-review NARROWED BLOCKER — admit-capable re-admission). ─────
    //
    // The data-layer integration of the round-2 fix. The prior data-layer test used a PLAIN member (no
    // members:admit) whose forged admission is dropped at the authority check. Here Mallory is admitted as an
    // ADMIN (legitimately holds members:admit), then signs a FRESH, fully-valid admission of an existing
    // participant (Alice) carrying her OWN DM key — which passes signature + consistency + authority +
    // no-escalation. Under the old last-write-wins resolution this OVERWROTE Alice's binding on the victim node.
    // First-write-wins drops it: the victim STILL resolves Alice's authentic key. Driven through the full
    // RosterCrdtProjection → RebuildLiveRoster → NodeTeamRoster.DmPublicKeyOf path.

    [Fact(DisplayName = "C5 LEAK TEST (data layer, admit-capable): an ADMIN re-admitting an existing participant with a substituted DM key cannot overwrite the victim-node binding — first-write-wins")]
    public async Task AdmitCapable_Substituted_Dm_Key_Synced_Delta_Is_Rejected_On_Victim()
    {
        var founder = Identity.New("founder");
        var alice = Identity.New("alice");     // the victim DM participant.
        var mallory = Identity.New("mallory"); // an ADMIN (holds members:admit) — the re-admission attacker.
        var bob = Identity.New("bob");         // the other DM participant — the node we check resolves Alice's key.

        var aliceAuthenticDm = DmKeyOf("alice"); // Alice's REAL, signed-into-admission DM key (her first write).
        var malloryDm = DmKeyOf("mallory");      // Mallory's own DM key — what she substitutes for Alice's.

        // A founds the team; admits Alice (Member, with her signed DM key) FIRST, then Mallory as an ADMIN, then Bob.
        var a = await NewReplicaAsync("A", founder);
        await SeedLocalAdmissionsAsync(a);
        var aliceIssuedAt = DateTimeOffset.UnixEpoch.AddSeconds(1000); // earlier than the attack.
        var aliceRoster = a.NodeRoster.Current.Admit(
            founder.PartyId, founder.Signer, "alice", alice.Key.PrincipalId,
            PermissionCompositions.Member, Verifier, aliceIssuedAt, Guid.NewGuid(),
            newDmPublicKey: aliceAuthenticDm);
        var withMallory = aliceRoster.Admit(
            founder.PartyId, founder.Signer, "mallory", mallory.Key.PrincipalId,
            PermissionCompositions.Admin, Verifier, aliceIssuedAt, Guid.NewGuid(),
            newDmPublicKey: malloryDm);
        var withBob = withMallory.Admit(
            founder.PartyId, founder.Signer, "bob", bob.Key.PrincipalId,
            PermissionCompositions.Member, Verifier, aliceIssuedAt, Guid.NewGuid(),
            newDmPublicKey: DmKeyOf("bob"));
        a.NodeRoster.AdoptSyncedRoster(withBob);
        foreach (var party in new[] { "alice", "mallory", "bob" })
        {
            var rec = withBob.EnumerateAdmissions().Single(x => x.PartyId == party);
            await a.Projection.PublishLocalAsync(RosterRecordCrdtState.FromAdmission(rec), CancellationToken.None);
        }

        var bobNode = await NewJoinerReplicaAsync("Bob", a.NodeRoster.Current, founder.Signer);

        // ── THE ATTACK. Mallory (ADMIN) signs a FRESH, fully-valid admission of the existing participant Alice,
        //    over Alice's real principal key, carrying her OWN DM key — LATER issuance, fresh nonce. She IS
        //    authorized to admit, so this passes every gate except first-write-wins. She publishes it onto A's
        //    doctype (modeling a malicious admin injecting the delta into the shared roster). ──────────────────────
        var malloryReAdmit = RosterSigning.SignAdmission(
            signer: mallory.Signer, teamId: Team, admittedPartyId: "alice",
            admittedPublicKey: alice.Key.PrincipalId, admittedByPartyId: "mallory",
            isGenesis: false, issuedAt: DateTimeOffset.UnixEpoch.AddSeconds(2000), nonce: Guid.NewGuid(),
            admittedDmPublicKey: malloryDm, admittedPermissions: PermissionCompositions.Member);
        var malloryRecord = new MemberAdmissionRecord(
            Team.ToString("D"), "alice", alice.Key.PrincipalId, PermissionCompositions.Member,
            malloryReAdmit, TransportPublicKey: null,
            DmPublicKey: PrincipalId.FromBase64Url(malloryDm).AsSpan().ToArray());
        await a.Projection.PublishLocalAsync(
            RosterRecordCrdtState.FromAdmission(malloryRecord), CancellationToken.None);

        await SyncAsync(a, bobNode);

        // THE LEAK ASSERTION on the victim node: Alice's RESOLVED DM key is her AUTHENTIC first-write key — the
        // admin's later substitution did NOT overwrite it. Mallory (an authorized admitter) still cannot read
        // Alice's DMs.
        Assert.True(bobNode.NodeRoster.Current.Contains("alice"));
        Assert.True(bobNode.NodeRoster.Current.Contains("mallory"));

        var resolvedAliceDm = bobNode.NodeRoster.DmPublicKeyOf("alice");
        Assert.NotNull(resolvedAliceDm);
        var resolvedB64 = PrincipalId.FromBytes(resolvedAliceDm!).ToBase64Url();
        Assert.Equal(aliceAuthenticDm, resolvedB64);  // Alice's AUTHENTIC first-write key resolves.
        Assert.NotEqual(malloryDm, resolvedB64);       // the admin's substitute is REJECTED — no overwrite.
        Assert.True(bobNode.NodeRoster.Current.ValidatesToGenesis(Verifier));
    }
}
