using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Recovery;
using Harborline.Api.Foundation.Recovery.Erasure;
using Harborline.Api.Foundation.Recovery.TenantKey;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.LocalNodeHost.Data.Search;
using Harborline.Api.LocalNodeHost.Data.Search.Vector;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Search.Vector;

/// <summary>
/// F1 (Slice-1d, BINDING — the Slice-1b deep-review's F1 gate). The subject-erasure FLOW
/// (<see cref="ISubjectErasureService.EraseAsync"/>) must drop the crypto-shredded subject's DERIVED CLEARTEXT
/// <c>vec0</c> residue, not just its durable encrypted rows. Slice 1b built
/// <see cref="NodeVecIndexer.PurgeSubjectAsync"/> (which deletes both the durable rows and the cleartext sink
/// rows) but left it with NO erasure-flow caller — so on a real-<c>vec0</c> host a shred would leave the
/// cleartext bit code resident. The <see cref="KgVecIndexSubjectErasurePropagator"/> closes that: registered
/// into the recovery erasure flow, a real <c>EraseAsync</c> now purges the vec0 sink.
/// </summary>
/// <remarks>
/// The real <c>vec0</c> native is host-gated (Intel CI has none), so the cleartext-cache contract is proven
/// here against a recording <see cref="IVecAccelerationSink"/> that stands in for the <c>search_vec0</c>
/// table. The wire under test — erasure flow ⇒ propagator ⇒ purge — is identical whether the sink is the
/// recording stand-in or the real native; only the sink impl differs (covered by the F2 host-gated native
/// parity probe).
/// </remarks>
public sealed class VecErasureFlowF1Tests
{
    private const int Dim = 64;
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddYears(56);
    private static readonly TenantId TenantA = TenantId.FromString("tenant-A");
    private static readonly SubjectId Alice = new("subject-alice");
    private static readonly SubjectId Bob = new("subject-bob");

    [Fact(DisplayName = "F1: a subject crypto-shred via the ERASURE FLOW drops the cleartext vec0 residue (durable rows AND cleartext sink) — bob survives")]
    public async Task EraseFlow_Purges_Cleartext_Vec0_Residue()
    {
        await using var h = await VecTestHarness.CreateAsync();
        var sink = new RecordingVec0Sink();
        var indexer = h.IndexerWithSink(sink, dimension: Dim);

        await indexer.IndexRecordAsync("inv-alice-1", "tenant-A", "subject-alice", "rent one", SearchResidency.Cache);
        await indexer.IndexRecordAsync("inv-alice-2", "tenant-A", "subject-alice", "rent two", SearchResidency.Cache);
        await indexer.IndexRecordAsync("inv-bob", "tenant-A", "subject-bob", "rent three", SearchResidency.Cache);

        // Pre-shred: the cleartext vec0 sink holds the partially-invertible bit code for BOTH subjects.
        Assert.Equal(new[] { "inv-alice-1", "inv-alice-2", "inv-bob" }, sink.RecordIdsSorted());

        // Build the REAL erasure flow with the KG-vec propagator wired in (the F1 wire), and run a valid shred.
        var erasureSvc = BuildErasureServiceWithPropagator(h.Erasure, indexer);
        var result = await erasureSvc.EraseAsync(ValidRequest(Alice), CancellationToken.None);
        Assert.Equal(SubjectErasureOutcome.Erased, result.Outcome);

        // F1 ASSERTION: the cleartext vec0 sink leaves ZERO residue of the shredded subject (the partially-
        // invertible bit code is gone), while bob's cleartext row survives.
        Assert.Equal(new[] { "inv-bob" }, sink.RecordIdsSorted());

        // And the durable per-subject-encrypted rows for the shredded subject are gone too; bob remains.
        await using var ctx = h.Store.CreateContext();
        var durable = await ctx.VecRows.Select(v => v.RecordId).ToListAsync();
        Assert.Equal(new[] { "inv-bob" }, durable);
    }

    [Fact(DisplayName = "F1 NON-VACUOUS: WITHOUT the propagator wired, an erasure-flow shred leaves the cleartext vec0 residue resident (the gap the wire closes)")]
    public async Task NonVacuous_WithoutPropagator_CleartextResidueRemains()
    {
        await using var h = await VecTestHarness.CreateAsync();
        var sink = new RecordingVec0Sink();
        var indexer = h.IndexerWithSink(sink, dimension: Dim);

        await indexer.IndexRecordAsync("inv-alice-1", "tenant-A", "subject-alice", "rent one", SearchResidency.Cache);
        await indexer.IndexRecordAsync("inv-bob", "tenant-A", "subject-bob", "rent three", SearchResidency.Cache);

        // The SAME erasure flow, but with NO propagator registered (the pre-F1 state).
        var erasureSvc = BuildErasureServiceWithoutPropagator(h.Erasure);
        var result = await erasureSvc.EraseAsync(ValidRequest(Alice), CancellationToken.None);
        Assert.Equal(SubjectErasureOutcome.Erased, result.Outcome);

        // The durable encrypted row is undecryptable (sub-key gone) — the shred boundary is closed regardless.
        // BUT the cleartext vec0 residue for the shredded subject SURVIVES: this is exactly the leak F1 closes,
        // so this test would PASS-as-leak only when the wire is absent — proving the wire's test is non-vacuous.
        Assert.Contains("inv-alice-1", sink.RecordIdsSorted());
    }

    private static SubjectErasureService BuildErasureServiceWithPropagator(
        InMemorySubjectErasureRegistry registry, NodeVecIndexer indexer) =>
        new(
            registry,
            new InMemorySubjectTombstoneStore(),
            new NoopAuditTrail(),
            new Ed25519Signer(KeyPair.Generate()),
            new NoopTenantKeyDestroyer(),
            new FixedClock(Now),
            TimeSpan.Zero,
            new ISubjectErasurePropagator[] { new KgVecIndexSubjectErasurePropagator(indexer) });

    private static SubjectErasureService BuildErasureServiceWithoutPropagator(
        InMemorySubjectErasureRegistry registry) =>
        new(
            registry,
            new InMemorySubjectTombstoneStore(),
            new NoopAuditTrail(),
            new Ed25519Signer(KeyPair.Generate()),
            new NoopTenantKeyDestroyer(),
            new FixedClock(Now),
            TimeSpan.Zero,
            propagators: null);

    private static SubjectErasureRequest ValidRequest(SubjectId subject) => new(
        TenantA, subject, Now,
        new[] { new ActorId("captain"), new ActorId("officer") }, "erasure-ticket");

    /// <summary>An in-memory stand-in for the derived cleartext <c>vec0</c> acceleration table.</summary>
    private sealed class RecordingVec0Sink : IVecAccelerationSink
    {
        private readonly Dictionary<string, byte[]> _rows = new(StringComparer.Ordinal);

        public Task UpsertAsync(string recordId, string tenantId, byte[] packedCode, CancellationToken ct)
        {
            _rows[recordId] = packedCode;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string recordId, CancellationToken ct)
        {
            _rows.Remove(recordId);
            return Task.CompletedTask;
        }

        public string[] RecordIdsSorted() => _rows.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
    }

    private sealed class FixedClock : IRecoveryClock
    {
        private readonly DateTimeOffset _instant;
        public FixedClock(DateTimeOffset instant) => _instant = instant;
        public DateTimeOffset UtcNow() => _instant;
    }

    private sealed class NoopAuditTrail : IAuditTrail
    {
        public ValueTask AppendAsync(AuditRecord record, CancellationToken ct = default) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<AuditRecord> QueryAsync(
            AuditQuery query,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class NoopTenantKeyDestroyer : ITenantKeyDestroyer
    {
        public Task DestroySubjectKeysAsync(TenantId tenant, SubjectId subject, CancellationToken ct) =>
            Task.CompletedTask;

        public Task DestroyTenantKeysAsync(TenantId tenant, CancellationToken ct) => Task.CompletedTask;
    }
}
