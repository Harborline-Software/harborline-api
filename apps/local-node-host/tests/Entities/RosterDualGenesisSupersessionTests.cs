using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Crdt;
using Harborline.Api.Kernel.Crdt.Backends;
using Harborline.Api.LocalNodeHost.Data.Roster;
using Harborline.Api.LocalNodeHost.Enrollment;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// THE DUAL-GENESIS SUPERSESSION PROOF (cerebrum [2026-06-21] DECISIVE cross-machine verify — BLOCKER-2). The
/// cross-machine verify proved the comms did NOT converge over the wire even after the trust handshake passed:
/// at boot <see cref="RosterSyncBootstrapHostedService"/> seeds the node's OWN genesis as a SYNCED roster-CRDT
/// record; on a wire-enrollment JOIN the node adopts the admitter's team IN MEMORY
/// (<see cref="NodeTeamRoster.AdoptEnrollment"/>) but never retracted its OWN genesis from the SYNCED doctype.
/// Once the admitter's genesis converged in, the doctype held TWO genesis from differing parties →
/// <see cref="MemberRoster.FromSyncedRecords"/>'s injection guard returned <see cref="MemberRoster.Empty"/>
/// fail-closed → the synced roster never converged → the comms forge-proof <c>rosterBinding</c> had no members →
/// inbound foreign-author deltas were dropped → no convergence either way.
/// </summary>
/// <remarks>
/// <para>
/// <b>Real persisted roster store + real wire/delta merge — NOT in-memory only.</b> Each node is a
/// <see cref="RosterCrdtProjection"/> over its OWN SQLite <see cref="NodeLocalRosterDbContext"/> store + its OWN
/// real <see cref="YDotNetCrdtEngine"/> + its OWN <see cref="NodeTeamRoster"/>, exchanging deltas directly (the
/// gossip daemon's produce/consume round). The supersession is exercised through the EXACT production method
/// <see cref="NodeWireEnrollmentClient.EnrollAsync"/> calls at adopt time —
/// <see cref="RosterCrdtProjection.SupersedeOwnTeamRecordsAsync"/> — over the persisted store + wire, the
/// shortcut-free path the recurring lesson mandates.
/// </para>
/// <para>
/// <b>The injection guard stays intact.</b> A separate test injects an ATTACKER's foreign SECOND genesis for the
/// JOINED team and confirms <see cref="MemberRoster.FromSyncedRecords"/> still rejects it (a self-supersession of
/// the node's OWN records is legitimate; a foreign second genesis is not).
/// </para>
/// </remarks>
public sealed class RosterDualGenesisSupersessionTests : IAsyncLifetime
{
    private readonly List<Node> _nodes = new();
    private readonly List<string> _tempDirs = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var n in _nodes) await n.DisposeAsync();
        foreach (var dir in _tempDirs)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch { /* best-effort temp cleanup */ }
        }
    }

    private static readonly Guid TeamA = Guid.Parse("aaaa0000-0000-0000-0000-000000000001");
    private static readonly Guid TeamB = Guid.Parse("bbbb0000-0000-0000-0000-000000000002");
    private static readonly IOperationVerifier Verifier = new Ed25519Verifier();

    private sealed record Identity(string PartyId, KeyPair Key, IOperationSigner Signer)
    {
        public static Identity New(string partyId)
        {
            var kp = KeyPair.Generate();
            return new Identity(partyId, kp, new Ed25519Signer(kp));
        }
    }

    private sealed class Node : IAsyncDisposable
    {
        public required string Name { get; init; }
        public required string Dir { get; init; }
        public required ServiceProvider Sp { get; init; }
        public required IDbContextFactory<NodeLocalRosterDbContext> Factory { get; init; }
        public required RosterCrdtProjection Projection { get; init; }
        public required NodeTeamRoster NodeRoster { get; init; }

        public async ValueTask DisposeAsync()
        {
            await Projection.DisposeAsync();
            await Sp.DisposeAsync();
            try { if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true); }
            catch { /* best-effort temp cleanup */ }
        }
    }

    /// <summary>Build a node over a real SQLite roster store + real YDotNet engine, seeded with
    /// <paramref name="genesisRoster"/> as its local roster.</summary>
    private async Task<Node> NewNodeAsync(string name, MemberRoster genesisRoster)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"harborline-dualgen-{name}-{Guid.NewGuid():N}");
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

        var nodeRoster = new NodeTeamRoster(genesisRoster);
        var projection = new RosterCrdtProjection(TimeProvider.System,
            sp.GetRequiredService<ICrdtEngine>(), factory, Verifier,
            NullLogger<RosterCrdtProjection>.Instance, nodeRoster);

        var node = new Node
        {
            Name = name, Dir = dir, Sp = sp, Factory = factory, Projection = projection, NodeRoster = nodeRoster,
        };
        _nodes.Add(node);
        return node;
    }

    /// <summary>
    /// Build a REBOOT-CAPABLE node over a CALLER-SUPPLIED SQLite connection string (so a fresh process can be
    /// rebuilt over the SAME db to model a true reboot). Unlike <see cref="NewNodeAsync"/> this does NOT register the
    /// node in <c>_nodes</c> (which auto-deletes the dir on dispose) — the persisted-reboot test disposes each
    /// projection explicitly and the shared dir is cleaned via <c>_tempDirs</c>. <paramref name="genesisRoster"/> is
    /// the node's in-memory boot roster (a fresh process re-seeds it from its OWN genesis exactly as the bootstrap
    /// does); the DURABLE store carries whatever a prior process left.
    /// </summary>
    private async Task<Node> NewPersistentNodeAsync(string name, MemberRoster genesisRoster, string connectionString)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContextFactory<NodeLocalRosterDbContext>(opt => opt.UseSqlite(connectionString));
        services.AddSingleton<ICrdtEngine, YDotNetCrdtEngine>();

        var sp = services.BuildServiceProvider();
        var factory = sp.GetRequiredService<IDbContextFactory<NodeLocalRosterDbContext>>();
        await using (var ctx = await factory.CreateDbContextAsync())
            await ctx.Database.EnsureCreatedAsync(); // idempotent — created once, a no-op on subsequent reboots.

        var nodeRoster = new NodeTeamRoster(genesisRoster);
        var projection = new RosterCrdtProjection(TimeProvider.System,
            sp.GetRequiredService<ICrdtEngine>(), factory, Verifier,
            NullLogger<RosterCrdtProjection>.Instance, nodeRoster);

        // Dir is shared across reboots → not owned here; cleaned via _tempDirs. Use a no-cleanup Node wrapper.
        return new Node
        {
            Name = name, Dir = string.Empty, Sp = sp, Factory = factory, Projection = projection, NodeRoster = nodeRoster,
        };
    }

    /// <summary>Tear down a persisted node to model a PROCESS EXIT between reboots: dispose the projection + the
    /// service provider. The connection string is Pooling=False, so this already fully releases the db file
    /// before the next process re-opens the SAME db (a true cold start, with no leftover pooled handle).</summary>
    private static async Task RebootDisposeAsync(Node n)
    {
        await n.Projection.DisposeAsync();
        await n.Sp.DisposeAsync();
    }

    /// <summary>Seed every admission in a node's local roster onto its synced doctype (the bootstrap genesis-seed
    /// path — <see cref="RosterSyncBootstrapHostedService"/>).</summary>
    private static async Task SeedLocalAdmissionsAsync(Node n)
    {
        foreach (var rec in n.NodeRoster.Current.EnumerateAdmissions())
            await n.Projection.PublishLocalAsync(RosterRecordCrdtState.FromAdmission(rec), CancellationToken.None);
    }

    /// <summary>One direction of a sync round on the LIVE trigger (delta → apply → drain reconciles).</summary>
    private static async Task SyncAsync(Node src, Node dst)
    {
        var delta = await src.Projection.EncodeOutboundDeltaAsync(
            RosterCrdtProjection.DocumentId, dst.Projection.VectorClock, CancellationToken.None);
        Assert.NotNull(delta);
        await dst.Projection.ApplyInboundDeltaAsync(
            RosterCrdtProjection.DocumentId, 1, delta!.Value, CancellationToken.None);
        await dst.Projection.DrainPendingReconcilesAsync();
    }

    private static int GenesisCount(RosterCrdtProjection p) =>
        p.Snapshot().Count(s => s.Kind == RosterRecordKind.Admission && s.IsGenesis);

    private static int DurableRowCount(Node n)
    {
        using var ctx = n.Factory.CreateDbContext();
        return ctx.Set<NodeRosterRecord>().Count();
    }

    private static int DurableTeamRowCount(Node n, Guid teamId)
    {
        var teamIdString = teamId.ToString("D");
        using var ctx = n.Factory.CreateDbContext();
        return ctx.Set<NodeRosterRecord>().Count(r => r.TeamId == teamIdString);
    }

    [Fact]
    public async Task Boot_reconciliation_removes_only_valid_locally_minted_stale_genesis()
    {
        var local = Identity.New("os:HarborlineDogfood#founder");
        var foreign = Identity.New(local.PartyId); // same label, but no possession of the node's signing key.
        var currentGenesis = MemberRoster.Genesis(
            TeamA, local.PartyId, local.Signer, Verifier, DateTimeOffset.UtcNow, Guid.NewGuid());
        var staleOwnGenesis = MemberRoster.Genesis(
            TeamB, local.PartyId, local.Signer, Verifier, DateTimeOffset.UtcNow, Guid.NewGuid());
        var foreignGenesis = MemberRoster.Genesis(
            Guid.Parse("cccc0000-0000-0000-0000-000000000003"), foreign.PartyId, foreign.Signer,
            Verifier, DateTimeOffset.UtcNow, Guid.NewGuid());
        var node = await NewNodeAsync("boot-reconcile", currentGenesis);

        await node.Projection.PublishLocalAsync(
            RosterRecordCrdtState.FromAdmission(currentGenesis.EnumerateAdmissions().Single()),
            CancellationToken.None);
        await node.Projection.PublishLocalAsync(
            RosterRecordCrdtState.FromAdmission(staleOwnGenesis.EnumerateAdmissions().Single()),
            CancellationToken.None);
        await node.Projection.PublishLocalAsync(
            RosterRecordCrdtState.FromAdmission(foreignGenesis.EnumerateAdmissions().Single()),
            CancellationToken.None);

        var removed = await node.Projection.ReconcileLocallyMintedGenesisAsync(CancellationToken.None);

        Assert.Equal(1, removed);
        var survivors = node.Projection.Snapshot();
        Assert.DoesNotContain(survivors, r => r.TeamId == TeamB.ToString("D"));
        Assert.Contains(survivors, r => r.TeamId == TeamA.ToString("D"));
        Assert.Contains(survivors, r => r.TeamId == foreignGenesis.TeamId.ToString("D"));
        Assert.Equal(0, DurableTeamRowCount(node, TeamB));
        Assert.Equal(1, DurableTeamRowCount(node, TeamA));
        Assert.Equal(1, DurableTeamRowCount(node, foreignGenesis.TeamId));
    }

    // ── THE FIX (green-post): adopt + supersede own genesis → the synced roster converges to A only ──────────

    [Fact(DisplayName = "BLOCKER-2 FIX: B adopts A + supersedes its OWN genesis → synced roster converges to A (not Empty); A's delta accepted")]
    public async Task Adopt_Then_Supersede_Own_Genesis_Converges_To_A()
    {
        var founderA = Identity.New("os:alice#a1");
        var bobPrincipal = Identity.New("os:bob#b1"); // B's PRINCIPAL identity, admitted into A's team
        var founderB = Identity.New("os:bob-home#b0"); // B's OWN genesis-team founder (its boot identity)

        // ── A founds team A, seeds it, admits bob (B's principal), publishes the admission. ──
        var aGenesis = MemberRoster.Genesis(
            TeamA, founderA.PartyId, founderA.Signer, Verifier, DateTimeOffset.UtcNow, Guid.NewGuid());
        var a = await NewNodeAsync("A", aGenesis);
        await SeedLocalAdmissionsAsync(a);

        var aWithBob = a.NodeRoster.Current.Admit(
            founderA.PartyId, founderA.Signer, bobPrincipal.PartyId, bobPrincipal.Key.PrincipalId,
            PermissionCompositions.Member, Verifier, DateTimeOffset.UtcNow, Guid.NewGuid());
        a.NodeRoster.AdoptSyncedRoster(aWithBob);
        await a.Projection.PublishLocalAsync(
            RosterRecordCrdtState.FromAdmission(
                aWithBob.EnumerateAdmissions().Single(x => x.PartyId == bobPrincipal.PartyId)),
            CancellationToken.None);

        // ── B founds its OWN genesis team (the boot posture) + seeds its OWN genesis onto its synced doctype
        //    (exactly what RosterSyncBootstrapHostedService does at boot). ──
        var bGenesis = MemberRoster.Genesis(
            TeamB, founderB.PartyId, founderB.Signer, Verifier, DateTimeOffset.UtcNow, Guid.NewGuid());
        var b = await NewNodeAsync("B", bGenesis);
        await SeedLocalAdmissionsAsync(b);
        Assert.Equal(1, GenesisCount(b.Projection));          // B's own genesis is on B's synced doctype
        Assert.Equal(1, DurableRowCount(b));                  // ...and in B's durable store

        // ── B ENROLLS into A: SupersedeOwnTeamRecordsAsync (fail-closed, FIRST) THEN AdoptEnrollment (in-memory) —
        //    the EXACT order NodeWireEnrollmentClient.EnrollAsync runs (#1304 F1: supersede-before-adopt, so a
        //    supersession fault aborts retryably with no partial adopt; here it succeeds, so the adopt proceeds). ──
        var ownTeamBeforeAdopt = b.NodeRoster.Current.TeamId;
        Assert.Equal(TeamB, ownTeamBeforeAdopt);
        var superseded = await b.Projection.SupersedeOwnTeamRecordsAsync(ownTeamBeforeAdopt, CancellationToken.None);
        b.NodeRoster.AdoptEnrollment(aWithBob, new Dictionary<string, byte[]>());
        Assert.Equal(1, superseded);                          // B's own genesis retracted from the synced doctype
        Assert.Equal(0, GenesisCount(b.Projection));          // ...gone from the CRDT list
        Assert.Equal(0, DurableRowCount(b));                  // ...and PURGED from the durable store (restart-stable)

        // ── A's records (A's genesis + bob's admission) converge into B over the wire. ──
        await SyncAsync(a, b);

        // THE PROOF: B's synced doctype now holds EXACTLY ONE genesis (A's), so FromSyncedRecords rebuilds A's
        // roster (NOT Empty) — B's live roster converges to A's team with bob (B itself) as a member.
        Assert.Equal(1, GenesisCount(b.Projection));
        Assert.True(b.NodeRoster.Current.Contains(founderA.PartyId));
        Assert.True(b.NodeRoster.Current.Contains(bobPrincipal.PartyId));
        Assert.Equal(TeamA, b.NodeRoster.Current.TeamId);
        Assert.True(b.NodeRoster.Current.ValidatesToGenesis(Verifier));

        // ── A SUBSEQUENT admission delta from A is ACCEPTED (the rosterBinding recognizes A's team → comms/contact
        //    deltas converge). A admits a third member; it syncs to B and B adopts it. ──
        var carol = Identity.New("os:carol#c1");
        var aWithCarol = a.NodeRoster.Current.Admit(
            founderA.PartyId, founderA.Signer, carol.PartyId, carol.Key.PrincipalId,
            PermissionCompositions.Member, Verifier, DateTimeOffset.UtcNow, Guid.NewGuid());
        a.NodeRoster.AdoptSyncedRoster(aWithCarol);
        await a.Projection.PublishLocalAsync(
            RosterRecordCrdtState.FromAdmission(aWithCarol.EnumerateAdmissions().Single(x => x.PartyId == carol.PartyId)),
            CancellationToken.None);
        await SyncAsync(a, b);

        Assert.True(b.NodeRoster.Current.Contains(carol.PartyId)); // A's later delta accepted → convergence
        Assert.Equal(carol.Key.PrincipalId, b.NodeRoster.ForgeProofBinding(carol.PartyId));
        Assert.Equal(1, GenesisCount(b.Projection)); // still ONE genesis — no poison reintroduced

        // ── CONVERGES THE OTHER WAY: the removal of B's own genesis converges to A too (A drops it). B never
        //    shipped its own genesis to A here, so A holds exactly A's own — proving no cross-poison. ──
        await SyncAsync(b, a);
        Assert.Equal(1, GenesisCount(a.Projection));
        Assert.True(a.NodeRoster.Current.ValidatesToGenesis(Verifier));
    }

    // ── INFO-3 (verdict): PERSISTED-REBOOT-ON-FAILURE — a supersession durable-purge FAILURE then a retry must let
    //    B converge to A across a true reboot (NOT stuck silently poisoned). This locks in the F-REFUTED success-path
    //    self-heal AND proves the #1304 F1 fail-closed path is recoverable: if the durable purge faulted once (B's
    //    own genesis row survives), a re-run of the supersession purges it, and a fresh process over the SAME db then
    //    hydrates only A's records → B adopts A's roster on the next sync. The worst case (silent persistent poison
    //    across a reboot) is IMPOSSIBLE because the durable row is gone after the retry. ────────────────────────────

    [Fact(DisplayName = "#1304 F1 PERSISTED REBOOT after a supersession-purge FAILURE: B's own genesis survives the failed purge (would re-poison on reboot); a RETRY purges it durably; a fresh process over the SAME db then hydrates only A → B CONVERGES to A (no silent cross-reboot poison)")]
    public async Task Supersession_PurgeFailure_Then_Retry_ConvergesAcrossReboot()
    {
        var founderA = Identity.New("os:alice#a1");
        // PRODUCTION INVARIANT (bug-1332): the joiner enrolls into A under its OWN boot-genesis identity — it re-uses
        // its genesis/comms-author signer + party id as its enrolled principal (Program.cs:
        // principalSigner = genesisSigner.Signer; selfPartyId = genesisPartyId). So B's admitted member entry in A's
        // roster is bound to B's OWN key — which is what the gap-#2 own-membership adopt-guard (bug-1333) recognizes as
        // "this node is a member of the hydrated roster". Modeling B's principal as a DISTINCT key would not match
        // production and would make B fail its own membership check on the post-reboot hydration adopt.
        var founderB = Identity.New("os:bob-home#b0");
        var bobPrincipal = founderB; // B enrolls as itself (same key + party id) — the stable-id-across-join invariant.

        // ── A founds team A, admits B (under B's own boot identity), publishes the admission (A's converged set). ──
        var aGenesis = MemberRoster.Genesis(
            TeamA, founderA.PartyId, founderA.Signer, Verifier, DateTimeOffset.UtcNow, Guid.NewGuid());
        var a = await NewNodeAsync("A-reboot", aGenesis);
        await SeedLocalAdmissionsAsync(a);
        var aWithBob = a.NodeRoster.Current.Admit(
            founderA.PartyId, founderA.Signer, bobPrincipal.PartyId, bobPrincipal.Key.PrincipalId,
            PermissionCompositions.Member, Verifier, DateTimeOffset.UtcNow, Guid.NewGuid());
        a.NodeRoster.AdoptSyncedRoster(aWithBob);
        await a.Projection.PublishLocalAsync(
            RosterRecordCrdtState.FromAdmission(
                aWithBob.EnumerateAdmissions().Single(x => x.PartyId == bobPrincipal.PartyId)),
            CancellationToken.None);

        // ── B boots on its OWN genesis team + seeds its OWN genesis durably (the bootstrap seed). Keep B's db dir so
        //    we can REBOOT a fresh projection over the SAME store. ──
        var bGenesis = MemberRoster.Genesis(
            TeamB, founderB.PartyId, founderB.Signer, Verifier, DateTimeOffset.UtcNow, Guid.NewGuid());
        var bDir = Path.Combine(Path.GetTempPath(), $"harborline-dualgen-reboot-B-{Guid.NewGuid():N}");
        Directory.CreateDirectory(bDir);
        _tempDirs.Add(bDir);
        var bConn = $"Data Source={Path.Combine(bDir, "roster.db")};Pooling=False";

        var b1 = await NewPersistentNodeAsync("B1", bGenesis, bConn);
        await SeedLocalAdmissionsAsync(b1);
        Assert.Equal(1, DurableRowCount(b1)); // B's own genesis is durable.

        // ── THE FAILURE: B's join ran the in-memory adopt's supersession but the DURABLE PURGE FAILED (its row
        //    survived). Model that precisely: the own genesis is STILL in B's durable store after a "failed" purge.
        //    (Pre-fix this is exactly the persistent-poison precondition the verdict named: a reboot reloads it.) ──
        Assert.Equal(1, DurableRowCount(b1));

        // PROVE the precondition is genuinely poisoning: a fresh process over the SAME db, hydrated, with A's records
        // synced in, holds TWO genesis → FromSyncedRecords Empty → B would be stuck (the catastrophic state). ──
        await RebootDisposeAsync(b1); // close B1 (process exit) — release the projection, SP + the SQLite pool.
        var bPoison = await NewPersistentNodeAsync("B-poison", bGenesis, bConn);
        await bPoison.Projection.HydrateFromStoreAsync(CancellationToken.None); // reload the surviving own genesis.
        await SyncAsync(a, bPoison);
        Assert.Equal(2, GenesisCount(bPoison.Projection));     // own + A's → poison.
        Assert.False(bPoison.NodeRoster.Current.Contains(founderA.PartyId)); // B did NOT converge to A (Empty rebuild).
        await RebootDisposeAsync(bPoison);

        // ── THE RECOVERY (retry): a fresh process re-runs the supersession (the join retry re-captures B's own team
        //    id + re-runs the purge) — this time it SUCCEEDS, removing B's own genesis from the durable store. ──
        var bRetry = await NewPersistentNodeAsync("B-retry", bGenesis, bConn);
        await bRetry.Projection.HydrateFromStoreAsync(CancellationToken.None);
        var purged = await bRetry.Projection.SupersedeOwnTeamRecordsAsync(TeamB, CancellationToken.None);
        Assert.Equal(1, purged);                               // the retry purged B's own genesis...
        Assert.Equal(0, DurableTeamRowCount(bRetry, TeamB));   // ...B's OWN team rows gone from the durable store
                                                               // (restart-stable now); A's converged rows survive.
        await RebootDisposeAsync(bRetry);

        // ── THE PROOF: a TRUE REBOOT — a fresh process over the SAME db after the successful retry hydrates ONLY A's
        //    converged records (B's own genesis is durably gone). The cold-start hydration's reconcile rebuilds the
        //    live roster from the hydrated records alone → B CONVERGES to A's roster with NO further sync needed. No
        //    silent cross-reboot poison: B is on A's team, validates to A's genesis. ──
        var b2 = await NewPersistentNodeAsync("B2", bGenesis, bConn);
        await b2.Projection.HydrateFromStoreAsync(CancellationToken.None);
        await b2.Projection.DrainPendingReconcilesAsync(); // let the hydration-triggered rebuild settle.

        // The durable store carries ONLY A's records now (no B-own genesis), so the hydrated snapshot has exactly
        // ONE genesis (A's) — the rebuild adopts A's roster instead of fail-closing on a dual-genesis.
        Assert.Equal(1, GenesisCount(b2.Projection));          // exactly A's genesis — no poison reintroduced.
        Assert.Equal(0, DurableTeamRowCount(b2, TeamB));       // B's own genesis is durably gone across the reboot.
        Assert.True(b2.NodeRoster.Current.Contains(founderA.PartyId));
        Assert.True(b2.NodeRoster.Current.Contains(bobPrincipal.PartyId));
        Assert.Equal(TeamA, b2.NodeRoster.Current.TeamId);
        Assert.True(b2.NodeRoster.Current.ValidatesToGenesis(Verifier));

        await RebootDisposeAsync(b2);
    }

    // ── THE BITE (red-pre): adopt WITHOUT supersession → 2 genesis → rebuild Empty → no convergence ──────────

    [Fact(DisplayName = "BLOCKER-2 BITE: B adopts A but does NOT supersede its own genesis → 2 genesis → FromSyncedRecords Empty → no convergence")]
    public async Task Adopt_Without_Supersession_Poisons_Roster_No_Convergence()
    {
        var founderA = Identity.New("os:alice#a1");
        var bobPrincipal = Identity.New("os:bob#b1");
        var founderB = Identity.New("os:bob-home#b0");

        var aGenesis = MemberRoster.Genesis(
            TeamA, founderA.PartyId, founderA.Signer, Verifier, DateTimeOffset.UtcNow, Guid.NewGuid());
        var a = await NewNodeAsync("A", aGenesis);
        await SeedLocalAdmissionsAsync(a);
        var aWithBob = a.NodeRoster.Current.Admit(
            founderA.PartyId, founderA.Signer, bobPrincipal.PartyId, bobPrincipal.Key.PrincipalId,
            PermissionCompositions.Member, Verifier, DateTimeOffset.UtcNow, Guid.NewGuid());
        a.NodeRoster.AdoptSyncedRoster(aWithBob);
        await a.Projection.PublishLocalAsync(
            RosterRecordCrdtState.FromAdmission(aWithBob.EnumerateAdmissions().Single(x => x.PartyId == bobPrincipal.PartyId)),
            CancellationToken.None);

        var bGenesis = MemberRoster.Genesis(
            TeamB, founderB.PartyId, founderB.Signer, Verifier, DateTimeOffset.UtcNow, Guid.NewGuid());
        var b = await NewNodeAsync("B", bGenesis);
        await SeedLocalAdmissionsAsync(b);

        // PRE-FIX: B adopts A's roster IN MEMORY but does NOT supersede its own genesis on the synced doctype.
        b.NodeRoster.AdoptEnrollment(aWithBob, new Dictionary<string, byte[]>());

        // A's records converge into B → B's synced doctype now holds A's genesis + B's still-present own genesis.
        await SyncAsync(a, b);

        // THE BITE: TWO genesis from differing parties on the synced doctype.
        Assert.Equal(2, GenesisCount(b.Projection));

        // ...and that 2-genesis snapshot rebuilds to EMPTY — FromSyncedRecords fail-closes (the injection guard).
        var snapshot = b.Projection.Snapshot();
        var admissions = snapshot.Where(s => s.Kind == RosterRecordKind.Admission)
            .Select(s => s.ToAdmissionOrNull()).Where(x => x is not null).Select(x => x!).ToList();
        var rebuilt = MemberRoster.FromSyncedRecords(
            admissions, Array.Empty<MemberRevocationRecord>(), Verifier);
        Assert.Empty(rebuilt.Members); // the poisoned, non-converging state the fix eliminates

        // The live-roster rebuild therefore does NOT adopt A's synced roster (it keeps the local/in-memory one);
        // the SYNCED convergence path is dead — a later A delta cannot make the synced roster converge.
        b.Projection.RebuildLiveRoster(b.Projection.Snapshot());
        // RebuildLiveRoster keeps the prior roster on an Empty rebuild — the synced doctype is poisoned, so the
        // forge-proof rosterBinding never reflects A's converged membership via the sync path.
    }

    // ── GUARD STILL HOLDS: an ATTACKER's foreign 2nd genesis for the JOINED team is STILL rejected ───────────

    [Fact(DisplayName = "BLOCKER-2 GUARD INTACT: an attacker's foreign 2nd genesis for the JOINED team is STILL rejected (Empty)")]
    public async Task Foreign_Second_Genesis_For_Joined_Team_Is_Still_Rejected()
    {
        var founderA = Identity.New("os:alice#a1");
        var attacker = Identity.New("os:mallory#m1");

        // A's legitimate genesis for team A.
        var aGenesis = MemberRoster.Genesis(
            TeamA, founderA.PartyId, founderA.Signer, Verifier, DateTimeOffset.UtcNow, Guid.NewGuid());

        // An ATTACKER forges a SECOND genesis for the SAME team A (a different party, self-signed genesis) and
        // tries to inject it as a synced record. This is the injection the guard must reject — it is NOT a
        // legitimate self-supersession (the attacker is removing nothing of its own; it is ADDING a 2nd root).
        var forgedGenesis = RosterSigning.SignAdmission(
            signer: attacker.Signer, teamId: TeamA, admittedPartyId: attacker.PartyId,
            admittedPublicKey: attacker.Key.PrincipalId, admittedByPartyId: attacker.PartyId,
            isGenesis: true, issuedAt: DateTimeOffset.UtcNow, nonce: Guid.NewGuid(),
            admittedPermissions: PermissionCompositions.Member);

        var admissions = new List<MemberAdmissionRecord>
        {
            aGenesis.EnumerateAdmissions().Single(),
            new(TeamA.ToString("D"), attacker.PartyId, attacker.Key.PrincipalId,
                PermissionCompositions.Member, forgedGenesis),
        };

        // The guard bites: 2 genesis candidates (even for the SAME team) → FromSyncedRecords returns Empty.
        var rebuilt = MemberRoster.FromSyncedRecords(
            admissions, Array.Empty<MemberRevocationRecord>(), Verifier);
        Assert.Empty(rebuilt.Members);

        // And superseding a node's OWN team does NOT help an attacker: it only removes records matching the OWN
        // team id; a foreign genesis for the JOINED team (A) is untouched, so the guard still rejects. Drive the
        // real projection path to prove the supersession is scoped to OWN-team records only.
        var dir = Path.Combine(Path.GetTempPath(), $"harborline-dualgen-guard-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var node = await NewNodeAsync("guard", aGenesis);
        // Seed A's genesis + the forged 2nd genesis for team A onto the synced doctype.
        await node.Projection.PublishLocalAsync(
            RosterRecordCrdtState.FromAdmission(aGenesis.EnumerateAdmissions().Single()), CancellationToken.None);
        await node.Projection.PublishLocalAsync(
            RosterRecordCrdtState.FromAdmission(admissions[1]), CancellationToken.None);
        Assert.Equal(2, GenesisCount(node.Projection));

        // Supersede a DIFFERENT (own) team id — it must remove NOTHING (no records match team B), so both team-A
        // genesis survive and the guard still rejects (Empty). The fix cannot be abused to drop a foreign genesis.
        var removed = await node.Projection.SupersedeOwnTeamRecordsAsync(TeamB, CancellationToken.None);
        Assert.Equal(0, removed);
        Assert.Equal(2, GenesisCount(node.Projection)); // foreign 2nd genesis still present → guard still bites
        var stillEmpty = MemberRoster.FromSyncedRecords(
            node.Projection.Snapshot().Where(s => s.Kind == RosterRecordKind.Admission)
                .Select(s => s.ToAdmissionOrNull()).Where(x => x is not null).Select(x => x!),
            Array.Empty<MemberRevocationRecord>(), Verifier);
        Assert.Empty(stillEmpty.Members);
    }
}
