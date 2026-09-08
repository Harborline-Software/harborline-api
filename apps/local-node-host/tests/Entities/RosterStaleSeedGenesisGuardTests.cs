using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Kernel.Crdt;
using Harborline.Api.Kernel.Crdt.Backends;
using Harborline.Api.LocalNodeHost.Data.Roster;
using Harborline.Api.LocalNodeHost.Enrollment;
using Harborline.Api.LocalNodeHost.Health;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// THE app/DEV-SEED COLD-START CRASH (bug-1333). When the Harborline App spawns the node with a
/// <c>HARBORLINE_DEV_SEED_HEX</c> override but points it at a DURABLE store left over from a PRIOR root seed (a changed
/// dev seed, or a <c>HARBORLINE_DEV_DATA_DIR</c> reused across distinct seeds), the node crashed at boot with
/// <c>"Comms author binding is inconsistent: the seeded trust roster does not bind the active member … (gap #2)"</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The mechanism.</b> <c>RosterSyncBootstrapHostedService</c> runs cold-start hydration FIRST — it pushes the
/// durable <c>roster_records</c> onto the CRDT list, which fires the reconcile + <c>RebuildLiveRoster</c> BEFORE the
/// live, seed-derived genesis has been published as a synced record. A stale durable genesis (signed by the OLD seed's
/// key) therefore rebuilt cleanly and <c>AdoptSyncedRoster</c> REPLACED <see cref="NodeTeamRoster.Current"/> with it.
/// <see cref="HostedCommsApiEndpoint"/> then resolved <c>Current.GenesisPartyId</c> bound to the OLD key — which no
/// longer equals the comms signer derived from the CURRENT seed — and its forge-proof consistency assertion
/// fails-closed, crashing the host. The STANDALONE node never hit this: its store was seed-consistent.
/// </para>
/// <para>
/// <b>The fix.</b> <see cref="RosterCrdtProjection.RebuildLiveRoster"/> now refuses to adopt a rebuilt roster whose
/// genesis is NOT this node's live seed-derived genesis (same party id AND same bound key) — it keeps the local,
/// seed-correct roster (fail-safe, mirroring the dual-genesis guard). A legitimate team adoption (a joiner taking on an
/// admitter's team) does NOT come through this hydration rebuild — it runs through <c>NodeWireEnrollmentClient</c> /
/// own-team supersession — so this guard only blocks a STALE/FOREIGN durable genesis from silently overwriting the
/// live one.
/// </para>
/// <para>
/// These tests drive the REAL hydration path over a REAL persisted SQLite store + REAL YDotNet engine, then assert the
/// EXACT production fail-closed assertion in <see cref="HostedCommsApiEndpoint"/> by reproducing its consistency check
/// against the resulting live roster (red-pre / green-post), and prove the standalone (seed-consistent) path is
/// unaffected.
/// </para>
/// </remarks>
public sealed class RosterStaleSeedGenesisGuardTests : IAsyncLifetime
{
    private static readonly IOperationVerifier Verifier = new Ed25519Verifier();
    private static readonly Guid TeamId = Guid.Parse("ca12ca12-0000-0000-0000-00000000d133");

    private readonly List<string> _tempDirs = new();
    private readonly List<RosterCrdtProjection> _projections = new();
    private readonly List<ServiceProvider> _providers = new();
    private readonly List<NodePrincipalSigner> _signers = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var p in _projections) await p.DisposeAsync();
        foreach (var sp in _providers) await sp.DisposeAsync();
        foreach (var s in _signers) s.Dispose();
        foreach (var dir in _tempDirs)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch { /* best-effort temp cleanup */ }
        }
    }

    /// <summary>A node identity derived from a 32-byte seed (the Harborline App injects this via HARBORLINE_DEV_SEED_HEX →
    /// LocalNode__RootSeedHex). The party id mirrors Program.cs's gap-#2 derivation: os:&lt;user&gt;#&lt;key8&gt;.</summary>
    private sealed record SeedIdentity(string PartyId, NodePrincipalSigner Signer)
    {
        public static SeedIdentity From(byte seedFill, List<NodePrincipalSigner> sink)
        {
            var signer = new NodePrincipalSigner(Seed(seedFill));
            sink.Add(signer);
            // Mirror Program.cs gap-#2: 8-hex prefix of the signing key → per-seed-distinct party id.
            var key8 = Convert.ToHexString(signer.Signer.IssuerId.AsSpan()[..4]).ToLowerInvariant();
            return new SeedIdentity($"os:chris#{key8}", signer);
        }
    }

    private static byte[] Seed(byte fill)
    {
        var s = new byte[32];
        Array.Fill(s, fill);
        return s;
    }

    /// <summary>The genesis self-admission Program.cs seeds at boot for a given seed identity (StableGenesis — the
    /// same deterministic shape, so a restart re-derives byte-identically).</summary>
    private static MemberRoster GenesisFor(SeedIdentity id) =>
        MemberRoster.StableGenesis(TeamId, id.PartyId, id.Signer.Signer, Verifier);

    /// <summary>Build a node's RosterCrdtProjection over a caller-supplied SQLite connection (so a "reboot" can re-open
    /// the SAME durable store). <paramref name="bootGenesis"/> is the in-memory genesis the fresh process seeds.</summary>
    private async Task<(RosterCrdtProjection Projection, NodeTeamRoster Roster)> NewNodeAsync(
        MemberRoster bootGenesis, string connectionString)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContextFactory<NodeLocalRosterDbContext>(opt => opt.UseSqlite(connectionString));
        services.AddSingleton<ICrdtEngine, YDotNetCrdtEngine>();

        var sp = services.BuildServiceProvider();
        _providers.Add(sp);
        var factory = sp.GetRequiredService<IDbContextFactory<NodeLocalRosterDbContext>>();
        await using (var ctx = await factory.CreateDbContextAsync())
            await ctx.Database.EnsureCreatedAsync();

        var roster = new NodeTeamRoster(bootGenesis);
        var projection = new RosterCrdtProjection(TimeProvider.System,
            sp.GetRequiredService<ICrdtEngine>(), factory, Verifier,
            NullLogger<RosterCrdtProjection>.Instance, roster);
        _projections.Add(projection);
        return (projection, roster);
    }

    private string NewDbConn(string label)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"harborline-staleseed-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return $"Data Source={Path.Combine(dir, "roster.db")};Pooling=False";
    }

    /// <summary>Reproduce the EXACT HostedCommsApiEndpoint.ResolveAndAssertActiveMember consistency check against a
    /// live roster + a comms signer (the production fail-closed assertion). Returns true iff the binding is consistent
    /// (the operator's own messages would pass the forge-proof gate); false reproduces the boot crash.</summary>
    private static bool CommsAuthorBindingIsConsistent(NodeTeamRoster roster, NodePrincipalSigner commsSigner)
    {
        var partyId = roster.Current.GenesisPartyId;
        var boundKey = roster.Current.PublicKeyOf(partyId);
        var signerKey = commsSigner.Signer.IssuerId;
        return boundKey is not null && boundKey.Value.Equals(signerKey);
    }

    /// <summary>Seed a node's own genesis admission onto its synced doctype + durable store — exactly what
    /// RosterSyncBootstrapHostedService does at boot (so a later process hydrates it).</summary>
    private static async Task SeedGenesisDurablyAsync(RosterCrdtProjection projection, NodeTeamRoster roster)
    {
        foreach (var rec in roster.Current.EnumerateAdmissions())
            await projection.PublishLocalAsync(RosterRecordCrdtState.FromAdmission(rec), CancellationToken.None);
    }

    // ── THE CRASH REPRO: a stale-seed durable store + a fresh different-seed boot → pre-fix the live roster is
    //    overwritten by the stale genesis (comms binding inconsistent → boot crash); post-fix it is NOT. ───────────

    [Fact(DisplayName = "bug-1333: an app dev-seed CHANGE over a reused store — hydration must NOT adopt the stale-seed genesis; comms author binding stays consistent (no boot crash)")]
    public async Task DevSeedChange_Over_Reused_Store_KeepsLiveGenesis_CommsBindingConsistent()
    {
        var conn = NewDbConn("seedchange");

        // ── PRIOR RUN: the Harborline App spawned the node with seed-A; it seeded seed-A's genesis durably, then exited. ──
        var seedA = SeedIdentity.From(0xA1, _signers);
        var (projA, rosterA) = await NewNodeAsync(GenesisFor(seedA), conn);
        await SeedGenesisDurablyAsync(projA, rosterA);
        await projA.DisposeAsync();
        _projections.Remove(projA);
        // No global ClearAllPools(): the connection string is Pooling=False, so disposing projA already
        // released the file handle — modeling the process exit needs no process-wide pool clear
        // (bug-20260702-8012e553).

        // ── THIS RUN: the Harborline App re-spawns with a DIFFERENT HARBORLINE_DEV_SEED_HEX (seed-B) but the SAME data dir.
        //    The fresh process seeds seed-B's genesis in memory (Program.cs), then cold-start hydration reloads the
        //    DURABLE seed-A genesis. seed-A.PartyId != seed-B.PartyId and binds seed-A's key. ──
        var seedB = SeedIdentity.From(0xB2, _signers);
        Assert.NotEqual(seedA.PartyId, seedB.PartyId); // distinct identities (gap-#2 per-seed-distinct).
        var (projB, rosterB) = await NewNodeAsync(GenesisFor(seedB), conn);

        // Before hydration: the live roster is the seed-B genesis the boot just seeded — binding is consistent.
        Assert.Equal(seedB.PartyId, rosterB.Current.GenesisPartyId);
        Assert.True(CommsAuthorBindingIsConsistent(rosterB, seedB.Signer));

        // COLD-START HYDRATION — the exact RosterSyncBootstrapHostedService boot step that overwrote the live roster.
        await projB.HydrateFromStoreAsync(CancellationToken.None);
        await projB.DrainPendingReconcilesAsync(); // let the hydration-triggered rebuild settle.

        // THE FIX (green-post): the stale seed-A genesis is NOT adopted — the live roster stays seed-B's genesis, so
        // the comms author binding is still consistent and HostedCommsApiEndpoint would NOT crash at boot.
        // PRE-FIX this FAILS: RebuildLiveRoster adopted the seed-A genesis → Current.GenesisPartyId == seed-A,
        // bound to seed-A's key != seed-B comms signer → the consistency check returns false (the boot crash).
        Assert.Equal(seedB.PartyId, rosterB.Current.GenesisPartyId);
        Assert.True(
            CommsAuthorBindingIsConsistent(rosterB, seedB.Signer),
            "the comms author binding must stay bound to the live seed-derived genesis after cold-start hydration "
            + "of a stale-seed durable store (else HostedCommsApiEndpoint fails-closed at boot — bug-1333).");
    }

    // ── THE BITE (red-pre proof, fix-independent): hydrating ONLY a foreign-seed durable genesis rebuilds a roster
    //    whose genesis party != the live one. The guard refuses it; without the guard AdoptSyncedRoster would set it. ─

    [Fact(DisplayName = "bug-1333 BITE: a durable store whose ONLY genesis is from a different seed rebuilds a foreign genesis — the live seed-derived genesis must survive (guard refuses the stale adopt)")]
    public async Task ForeignSeedOnlyStore_RebuildsForeignGenesis_LiveGenesisSurvives()
    {
        var conn = NewDbConn("foreignonly");

        // A durable store seeded by seed-A only (the prior identity's genesis).
        var seedA = SeedIdentity.From(0xC3, _signers);
        var (projSeed, rosterSeed) = await NewNodeAsync(GenesisFor(seedA), conn);
        await SeedGenesisDurablyAsync(projSeed, rosterSeed);
        await projSeed.DisposeAsync();
        _projections.Remove(projSeed);

        // A fresh boot under seed-B over that store.
        var seedB = SeedIdentity.From(0xD4, _signers);
        var (projB, rosterB) = await NewNodeAsync(GenesisFor(seedB), conn);
        await projB.HydrateFromStoreAsync(CancellationToken.None);
        await projB.DrainPendingReconcilesAsync();

        // Prove the rebuild WOULD have produced a foreign (seed-A) genesis if adopted — the snapshot validates to a
        // single seed-A genesis, distinct from the live seed-B party. The guard is what stops AdoptSyncedRoster.
        var rebuilt = MemberRoster.FromSyncedRecords(
            projB.Snapshot().Where(s => s.Kind == RosterRecordKind.Admission)
                .Select(s => s.ToAdmissionOrNull()).Where(x => x is not null).Select(x => x!),
            Array.Empty<MemberRevocationRecord>(), Verifier);
        Assert.Single(rebuilt.Members);                  // the stale genesis rebuilds cleanly (it is self-consistent)...
        Assert.Equal(seedA.PartyId, rebuilt.GenesisPartyId); // ...as a FOREIGN (seed-A) genesis.

        // THE GUARD: the live roster was NOT overwritten — it remains the seed-B (live) genesis.
        Assert.Equal(seedB.PartyId, rosterB.Current.GenesisPartyId);
        Assert.True(CommsAuthorBindingIsConsistent(rosterB, seedB.Signer));
    }

    // ── NO REGRESSION: the standalone / seed-CONSISTENT path. A store seeded by the SAME seed the node boots under
    //    hydrates + adopts normally (the genesis matches), and the binding is consistent. ────────────────────────────

    [Fact(DisplayName = "bug-1333 NO REGRESSION: a seed-CONSISTENT durable store hydrates + adopts normally (the standalone path) — binding stays consistent")]
    public async Task SeedConsistentStore_HydratesAndAdoptsNormally_BindingConsistent()
    {
        var conn = NewDbConn("consistent");

        var seed = SeedIdentity.From(0xE5, _signers);

        // Prior run seeded this seed's genesis durably.
        var (proj1, roster1) = await NewNodeAsync(GenesisFor(seed), conn);
        await SeedGenesisDurablyAsync(proj1, roster1);
        await proj1.DisposeAsync();
        _projections.Remove(proj1);

        // Restart under the SAME seed (the standalone / LocalNode__RootSeedHex-stable path).
        var (proj2, roster2) = await NewNodeAsync(GenesisFor(seed), conn);
        await proj2.HydrateFromStoreAsync(CancellationToken.None);
        await proj2.DrainPendingReconcilesAsync();

        // The hydrated genesis == the live genesis (same seed) → adoption proceeds, the member is present, the
        // binding stays consistent. StableGenesis makes the hydrated + re-seeded records byte-identical (#1291 F1),
        // so there is exactly ONE genesis and the rebuild adopts it.
        Assert.Equal(seed.PartyId, roster2.Current.GenesisPartyId);
        Assert.True(roster2.Current.Contains(seed.PartyId));
        Assert.True(roster2.Current.ValidatesToGenesis(Verifier));
        Assert.True(CommsAuthorBindingIsConsistent(roster2, seed.Signer));
    }
}
