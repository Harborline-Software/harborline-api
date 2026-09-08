using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Blocks.People.Foundation.Data;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.Kernel.Crdt;
using Harborline.Api.Kernel.Crdt.Backends;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.People;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// Acceptance coverage for the contacts data↔delta CRDT bridge — the first synced doctype on the live
/// node-host path (multi-device INC-4). Proves the property the ONR depth survey named as the genuine
/// frontier (GAP-3): a contact edit emits a delta, applying a peer delta merges, and two replicas applying
/// each other's deltas reach the same state — and the converged state lands back in the readable EF store.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two in-proc replicas, NO networking.</b> Per the INC-4 acceptance gate, convergence is proven with
/// two in-process <see cref="ContactCrdtProjection"/> replicas (each over its own CRDT engine + its own
/// SQLite store) exchanging deltas directly — simulating the gossip daemon's produce/consume round. The
/// real cross-machine transport + listener flip is INC-1 / INC-5, out of scope here.
/// </para>
/// <para>
/// <b>Real YDotNet backend.</b> The convergence assertions exercise the production
/// <see cref="YDotNetCrdtEngine"/> (Yjs/yrs) — the stub's total-order replay cannot honestly test
/// concurrent-edit map LWW, so these tests pin the real backend.
/// </para>
/// </remarks>
public sealed class ContactCrdtConvergenceTests : IAsyncLifetime
{
    private readonly List<Replica> _replicas = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var r in _replicas) await r.DisposeAsync();
    }

    // ── A small in-proc replica: own CRDT engine, own SQLite store, own projection ──────────────────────
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
            try { if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true); }
            catch { /* best-effort temp cleanup */ }
        }
    }

    private async Task<Replica> NewReplicaAsync(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"harborline-contact-crdt-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var connectionString = $"Data Source={Path.Combine(dir, "local-node.db")};Pooling=False";

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHarborlineEntityModule, PeopleEntityModule>();
        services.AddDbContextFactory<LocalNodeDbContext>(opt => opt.UseSqlite(connectionString));
        // Real YDotNet backend — the convergence semantics must be honest.
        services.AddSingleton<ICrdtEngine, YDotNetCrdtEngine>();

        var sp = services.BuildServiceProvider();
        var factory = sp.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
        await using (var ctx = await factory.CreateDbContextAsync())
            await ctx.Database.EnsureCreatedAsync();

        var projection = new ContactCrdtProjection(
            sp.GetRequiredService<ICrdtEngine>(),
            factory,
            NullLogger<ContactCrdtProjection>.Instance);

        var replica = new Replica
        {
            Name = name, Dir = dir, Sp = sp, Factory = factory, Projection = projection,
        };
        _replicas.Add(replica);
        return replica;
    }

    private static readonly TenantId LocalTenant = new("local");
    private static readonly PartyId Actor = new("operator");

    /// <summary>
    /// Persist a contact create into a replica's EF store the way <see cref="ContactRoutes"/> does, then
    /// project it into the CRDT (EF-first, then project — the production write order).
    /// </summary>
    private static async Task<Party> CreateContactAsync(Replica r, string displayName, PartyKind kind = PartyKind.Person)
    {
        var party = Party.Create(LocalTenant, kind, displayName, Actor, new Instant(System.TimeProvider.System.GetUtcNow()));
        await using (var ctx = await r.Factory.CreateDbContextAsync())
        {
            ctx.Set<Party>().Add(party);
            await ctx.SaveChangesAsync();
        }
        r.Projection.ProjectUpsert(party);
        return party;
    }

    /// <summary>Mutate + re-persist a contact in a replica's EF store, then project (production order).</summary>
    private static async Task<Party> UpdateContactAsync(Replica r, Party party, string newDisplayName)
    {
        Party stamped;
        await using (var ctx = await r.Factory.CreateDbContextAsync())
        {
            var existing = await ctx.Set<Party>().IgnoreQueryFilters()
                .FirstAsync(p => p.Id == party.Id);
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

    /// <summary>Read a contact's DisplayName from a replica's readable EF store (the GET path's source).</summary>
    private static async Task<string?> ReadDisplayNameAsync(Replica r, PartyId id)
    {
        await using var ctx = await r.Factory.CreateDbContextAsync();
        var p = await ctx.Set<Party>().IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id);
        return p?.DeletedAt is null ? p?.DisplayName : null;
    }

    /// <summary>
    /// One direction of a sync round on the LIVE trigger: encode src's outbound delta against dst's vector
    /// clock, apply it to dst, then await the reconciles that the projection's own <c>Changed→reconcile</c>
    /// handler spawned. This is the production path — applying the delta is the only call; the EF write-back
    /// is driven by the map's <c>Changed</c> event, NOT by an explicit <c>ReconcileAsync</c>. (The earlier
    /// version of this helper called <c>ReconcileAsync</c> per id, which bypassed the live trigger and
    /// masked earlier repository ticket #1260 F1 — a value-only inbound change never reconciled live.) <c>contactIds</c> is
    /// retained for call-site readability but is intentionally unused: the live trigger reconciles whatever
    /// keys the delta actually changed.
    /// </summary>
    private static async Task SyncAsync(Replica src, Replica dst, params string[] contactIds)
    {
        _ = contactIds; // live trigger reconciles the changed keys; ids are documentation only
        var delta = await src.Projection.EncodeOutboundDeltaAsync(
            ContactCrdtProjection.DocumentId, dst.Projection.VectorClock, CancellationToken.None);
        Assert.NotNull(delta);
        await dst.Projection.ApplyInboundDeltaAsync(
            ContactCrdtProjection.DocumentId, 1, delta!.Value, CancellationToken.None);
        // Await the LIVE Changed→reconcile work — no explicit ReconcileAsync, so the test exercises exactly
        // the path the gossip daemon runs.
        await dst.Projection.DrainPendingReconcilesAsync();
    }

    // ── Test 1: a contact edit on A emits a delta that, applied on B, merges + lands in B's read store ──

    [Fact(DisplayName = "INC-4: create contact on A → delta → appears in B's readable store")]
    public async Task Create_On_A_Appears_On_B()
    {
        var a = await NewReplicaAsync("A");
        var b = await NewReplicaAsync("B");

        var party = await CreateContactAsync(a, "Ada Lovelace");

        // A's edit produced a non-empty delta (B has seen nothing yet).
        await SyncAsync(a, b, party.Id.Value);

        Assert.Equal("Ada Lovelace", b.Projection.GetState(party.Id.Value)!.DisplayName);
        Assert.Equal("Ada Lovelace", await ReadDisplayNameAsync(b, party.Id));
    }

    // ── Test 2: concurrent edits to the SAME contact converge to one state on both replicas ─────────────

    [Fact(DisplayName = "INC-4: concurrent edits to the SAME contact converge (CRDT property)")]
    public async Task Concurrent_Same_Contact_Converges()
    {
        var a = await NewReplicaAsync("A");
        var b = await NewReplicaAsync("B");

        // Both replicas start with the same contact (created on A, synced to B).
        var party = await CreateContactAsync(a, "Grace H.");
        await SyncAsync(a, b, party.Id.Value);

        // Concurrent edits: A and B each rename the SAME contact without seeing the other's edit.
        await UpdateContactAsync(a, party, "Grace Hopper (A)");
        await UpdateContactAsync(b, party, "Grace Hopper (B)");

        // Exchange both directions (each encodes against the other's pre-exchange clock).
        var aClockBefore = a.Projection.VectorClock;
        var bClockBefore = b.Projection.VectorClock;

        var aDelta = await a.Projection.EncodeOutboundDeltaAsync(ContactCrdtProjection.DocumentId, bClockBefore, CancellationToken.None);
        var bDelta = await b.Projection.EncodeOutboundDeltaAsync(ContactCrdtProjection.DocumentId, aClockBefore, CancellationToken.None);
        await b.Projection.ApplyInboundDeltaAsync(ContactCrdtProjection.DocumentId, 1, aDelta!.Value, CancellationToken.None);
        await a.Projection.ApplyInboundDeltaAsync(ContactCrdtProjection.DocumentId, 1, bDelta!.Value, CancellationToken.None);
        // LIVE trigger drives the EF write-back for the value-only concurrent edit — no explicit reconcile.
        await a.Projection.DrainPendingReconcilesAsync();
        await b.Projection.DrainPendingReconcilesAsync();

        var aState = a.Projection.GetState(party.Id.Value)!.DisplayName;
        var bState = b.Projection.GetState(party.Id.Value)!.DisplayName;

        // Convergence: both replicas agree on a single winner chosen by YDotNet's map LWW.
        Assert.Equal(aState, bState);
        Assert.Contains(aState, new[] { "Grace Hopper (A)", "Grace Hopper (B)" });

        // And the converged winner is reflected in BOTH readable EF stores.
        Assert.Equal(aState, await ReadDisplayNameAsync(a, party.Id));
        Assert.Equal(bState, await ReadDisplayNameAsync(b, party.Id));
    }

    // ── Test 3: concurrent edits to DIFFERENT contacts both land on both replicas ───────────────────────

    [Fact(DisplayName = "INC-4: concurrent edits to DIFFERENT contacts both converge")]
    public async Task Concurrent_Different_Contacts_Both_Land()
    {
        var a = await NewReplicaAsync("A");
        var b = await NewReplicaAsync("B");

        // A creates Alice; B creates Bob — concurrently, neither seen by the other.
        var alice = await CreateContactAsync(a, "Alice");
        var bob = await CreateContactAsync(b, "Bob");

        // Exchange both directions.
        var aClockBefore = a.Projection.VectorClock;
        var bClockBefore = b.Projection.VectorClock;
        var aDelta = await a.Projection.EncodeOutboundDeltaAsync(ContactCrdtProjection.DocumentId, bClockBefore, CancellationToken.None);
        var bDelta = await b.Projection.EncodeOutboundDeltaAsync(ContactCrdtProjection.DocumentId, aClockBefore, CancellationToken.None);
        await b.Projection.ApplyInboundDeltaAsync(ContactCrdtProjection.DocumentId, 1, aDelta!.Value, CancellationToken.None);
        await a.Projection.ApplyInboundDeltaAsync(ContactCrdtProjection.DocumentId, 1, bDelta!.Value, CancellationToken.None);
        // LIVE trigger drives both write-backs (each is a key-add on the receiving replica).
        await a.Projection.DrainPendingReconcilesAsync();
        await b.Projection.DrainPendingReconcilesAsync();

        // Both contacts present on both replicas (the convergent union — no edit lost).
        Assert.Equal("Alice", await ReadDisplayNameAsync(a, alice.Id));
        Assert.Equal("Bob", await ReadDisplayNameAsync(a, bob.Id));
        Assert.Equal("Alice", await ReadDisplayNameAsync(b, alice.Id));
        Assert.Equal("Bob", await ReadDisplayNameAsync(b, bob.Id));
    }

    // ── Test 4: a delete (tombstone) converges to the peer ──────────────────────────────────────────────

    [Fact(DisplayName = "INC-4: a tombstone (delete) converges and hides the contact from the peer read")]
    public async Task Delete_Tombstone_Converges()
    {
        var a = await NewReplicaAsync("A");
        var b = await NewReplicaAsync("B");

        var party = await CreateContactAsync(a, "Temp Contact");
        await SyncAsync(a, b, party.Id.Value);
        Assert.Equal("Temp Contact", await ReadDisplayNameAsync(b, party.Id));

        // Delete on A (tombstone-not-delete), project the tombstone, sync to B.
        Party tombstoned;
        await using (var ctx = await a.Factory.CreateDbContextAsync())
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
        a.Projection.ProjectDelete(tombstoned);
        await SyncAsync(a, b, party.Id.Value);

        // The tombstone converged: the contact is hidden from B's readable store.
        Assert.Null(await ReadDisplayNameAsync(b, party.Id));
        Assert.True(b.Projection.GetState(party.Id.Value)!.Deleted);
    }

    // ── Test 5: idempotence — applying the same delta twice is a no-op ──────────────────────────────────

    [Fact(DisplayName = "INC-4: applying the same inbound delta twice is idempotent")]
    public async Task Delta_Apply_Is_Idempotent()
    {
        var a = await NewReplicaAsync("A");
        var b = await NewReplicaAsync("B");

        var party = await CreateContactAsync(a, "Idem Potent");
        var delta = await a.Projection.EncodeOutboundDeltaAsync(
            ContactCrdtProjection.DocumentId, b.Projection.VectorClock, CancellationToken.None);

        await b.Projection.ApplyInboundDeltaAsync(ContactCrdtProjection.DocumentId, 1, delta!.Value, CancellationToken.None);
        await b.Projection.ApplyInboundDeltaAsync(ContactCrdtProjection.DocumentId, 2, delta!.Value, CancellationToken.None);
        // LIVE trigger: the first apply is a key-add → reconcile; the second is a true no-op (idempotent).
        await b.Projection.DrainPendingReconcilesAsync();

        Assert.Equal(1, b.Projection.Count);
        Assert.Equal("Idem Potent", await ReadDisplayNameAsync(b, party.Id));
    }

    // ── Test 6 (the HONEST live-trigger test — earlier repository ticket #1260 F1) ─────────────────────────────────────────
    //
    // The previous suite proved convergence only because each test reached PAST the live trigger by calling
    // ReconcileAsync explicitly. This test exercises ONLY the production path — apply an inbound peer delta,
    // then await the reconciles the projection's own Changed handler spawned — and asserts the EF READ store
    // for a VALUE-ONLY inbound change (an UPDATE to an already-synced contact, AND a tombstone to one).
    //
    // PRE-FIX: the map's Changed event fired only on key add/remove, so a value-only inbound change raised
    // NO event, spawned NO reconcile, and the EF store stayed stale — these GET-path assertions FAIL.
    // POST-FIX: the map fires Changed on value diffs, the live reconcile runs, the EF store converges — PASS.
    //
    // Note: a brand-new contact's first arrival is a key-ADD (which always reconciled, even pre-fix), so the
    // contact is FIRST synced to B (key add), and only THEN does A push an update + a delete — guaranteeing
    // the inbound changes B sees are value-only changes to an already-present key, which is the exact gap.

    [Fact(DisplayName = "INC-4 (F1): an UPDATE to an already-synced contact reaches B's EF store via the LIVE trigger")]
    public async Task LiveTrigger_Update_To_Synced_Contact_Reaches_Ef_Store()
    {
        var a = await NewReplicaAsync("A");
        var b = await NewReplicaAsync("B");

        // 1. Create on A and sync to B so the contact's KEY is already present on B (a key-add — always
        //    reconciled, even pre-fix). After this, B's EF store holds "Original".
        var party = await CreateContactAsync(a, "Original");
        await SyncAsync(a, b, party.Id.Value);
        Assert.Equal("Original", await ReadDisplayNameAsync(b, party.Id));

        // 2. A renames the contact — a VALUE-ONLY change to a key B already has.
        await UpdateContactAsync(a, party, "RenamedByPeer");

        // 3. Apply A's delta on B over the LIVE trigger ONLY — no explicit ReconcileAsync anywhere.
        await SyncAsync(a, b, party.Id.Value);

        // The CRDT doc converged (this worked even pre-fix)…
        Assert.Equal("RenamedByPeer", b.Projection.GetState(party.Id.Value)!.DisplayName);
        // …AND the live trigger carried the value-only change into B's READ store (this is the F1 bite).
        Assert.Equal("RenamedByPeer", await ReadDisplayNameAsync(b, party.Id));
    }

    [Fact(DisplayName = "INC-4 (F1): a TOMBSTONE for an already-synced contact hides it from B's EF store via the LIVE trigger")]
    public async Task LiveTrigger_Tombstone_For_Synced_Contact_Hides_From_Ef_Store()
    {
        var a = await NewReplicaAsync("A");
        var b = await NewReplicaAsync("B");

        // 1. Create + sync so B already holds the contact (key-add).
        var party = await CreateContactAsync(a, "Doomed Contact");
        await SyncAsync(a, b, party.Id.Value);
        Assert.Equal("Doomed Contact", await ReadDisplayNameAsync(b, party.Id));

        // 2. Delete on A (tombstone-not-delete): the KEY stays present, only its value flips Deleted=true —
        //    a value-only change, which is exactly the gap (ProjectDelete does Set(...Deleted=true), not Remove).
        Party tombstoned;
        await using (var ctx = await a.Factory.CreateDbContextAsync())
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
        a.Projection.ProjectDelete(tombstoned);

        // 3. Apply A's tombstone delta on B over the LIVE trigger ONLY.
        await SyncAsync(a, b, party.Id.Value);

        // The tombstone converged in the doc…
        Assert.True(b.Projection.GetState(party.Id.Value)!.Deleted);
        // …AND the live trigger hid the contact from B's READ store (the F1 bite for deletes).
        Assert.Null(await ReadDisplayNameAsync(b, party.Id));
    }
}
