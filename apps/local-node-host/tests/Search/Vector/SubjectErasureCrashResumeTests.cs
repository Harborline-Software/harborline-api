using Microsoft.EntityFrameworkCore;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Recovery;
using Harborline.Api.Foundation.Recovery.Erasure;
using Harborline.Api.Foundation.Recovery.TenantKey;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.LocalNodeHost.Data.Audit;
using Harborline.Api.LocalNodeHost.Data.Search.Vector;
using Harborline.Api.LocalNodeHost.Tests.Search;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Search.Vector;

/// <summary>
/// DES-0029 kernel-core-ck-6: a subject erasure interrupted after its registry commit is finished by the retry —
/// tombstone, cleartext purge and the SubjectErased audit — each exactly once, over the real durable stores.
/// Faults are injected at the real tombstone table (SQLite trigger), the trail and the propagator.
/// </summary>
public sealed class SubjectErasureCrashResumeTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddYears(56);
    private static readonly TenantId Tenant = TenantId.FromString("tenant-erasure-resume");
    private static readonly SubjectId Alice = new("subject-alice");

    private SearchTestStore _store = null!;
    private readonly FlakyTrail _trail = new();
    private readonly CountingPropagator _propagator = new();

    public async Task InitializeAsync() => _store = await SearchTestStore.CreateAsync();

    public async Task DisposeAsync() => await _store.DisposeAsync();

    [Fact(DisplayName = "ck-6 erasure: a crash after the registry commit is finished by the retry — tombstone, purge and one audit")]
    public async Task CrashAfterRegistryCommit_RetryFinishesTheErasure()
    {
        await ExecuteAsync("CREATE TRIGGER ck6_fault BEFORE INSERT ON search_subject_tombstones BEGIN SELECT RAISE(ABORT, 'ck6'); END;");
        await Assert.ThrowsAnyAsync<Exception>(() => Service().EraseAsync(Request()));
        await ExecuteAsync("DROP TRIGGER ck6_fault;");

        var retry = await Service().EraseAsync(Request());

        Assert.NotNull(retry.Tombstone);
        Assert.Equal(1, await TombstonesAsync());
        Assert.Equal(1, _propagator.Calls);
        Assert.Single(await ErasedAuditsAsync());
    }

    [Fact(DisplayName = "ck-6 erasure: a crash before the audit is finished by the retry with exactly one SubjectErased entry")]
    public async Task CrashBeforeAudit_RetryAuditsOnce()
    {
        _trail.FailNextAppend = true;
        await Assert.ThrowsAnyAsync<Exception>(() => Service().EraseAsync(Request()));

        await Service().EraseAsync(Request());

        Assert.Single(await ErasedAuditsAsync());
        Assert.Equal(1, await TombstonesAsync());
        Assert.True(_propagator.Calls >= 1);
    }

    [Fact(DisplayName = "ck-6 erasure: a crash in the cleartext purge is retried, and the audit records the completed erasure once")]
    public async Task CrashInPurge_RetryPurgesAndAuditsOnce()
    {
        _propagator.FailNext = true;
        await Assert.ThrowsAnyAsync<Exception>(() => Service().EraseAsync(Request()));

        await Service().EraseAsync(Request());

        Assert.Equal(1, _propagator.Calls);
        Assert.Single(await ErasedAuditsAsync());
    }

    [Fact(DisplayName = "ck-6 erasure: a retry of a completed erasure is a no-op — no second audit, no second purge")]
    public async Task CompletedErasure_RetryIsANoOp()
    {
        Assert.Equal(SubjectErasureOutcome.Erased, (await Service().EraseAsync(Request())).Outcome);

        var retry = await Service().EraseAsync(Request());

        Assert.Equal(SubjectErasureOutcome.AlreadyErased, retry.Outcome);
        Assert.Equal(1, _propagator.Calls);
        Assert.Single(await ErasedAuditsAsync());
    }

    [Fact(DisplayName = "T-1048 ck-6 erasure: a crash after the registry commit, restarted over the same file, records one SubjectErased in the durable trail")]
    public async Task CrashAfterRegistryCommit_RestartOverTheSameFile_RecordsOneDurableAudit()
    {
        await ExecuteAsync("CREATE TRIGGER ck6_fault BEFORE INSERT ON search_subject_tombstones BEGIN SELECT RAISE(ABORT, 'ck6'); END;");
        await Assert.ThrowsAnyAsync<Exception>(() => DurableService(_store).EraseAsync(Request()));
        await ExecuteAsync("DROP TRIGGER ck6_fault;");
        Assert.True(await new NodeEfSubjectErasureRegistry(_store.Factory).IsErasedAsync(Tenant, Alice));
        Assert.Empty(await DurableErasedAuditsAsync(_store));

        // The process is gone. A new host opens the same file and the erasure request is retried, twice.
        await using var restarted = SearchTestStore.Reopen(_store);
        Assert.Equal(SubjectErasureOutcome.Erased, (await DurableService(restarted).EraseAsync(Request())).Outcome);
        Assert.Equal(SubjectErasureOutcome.AlreadyErased, (await DurableService(restarted).EraseAsync(Request())).Outcome);

        var audit = Assert.Single(await DurableErasedAuditsAsync(restarted));
        Assert.Equal(Now, audit.OccurredAt);
        Assert.Equal(1, await TombstonesAsync());
        Assert.Equal(1, _propagator.Calls);
    }

    [Fact(DisplayName = "T-1048 ck-6 erasure: an erasure refused before the registry commit records no erasure and no SubjectErased")]
    public async Task RefusedErasure_WritesNoRegistryRowAndNoAudit()
    {
        var single = new SubjectErasureRequest(Tenant, Alice, Now, [new ActorId("captain")], "erasure-ticket");

        await Assert.ThrowsAsync<SubjectErasureRejectedException>(() => DurableService(_store).EraseAsync(single));

        Assert.False(await new NodeEfSubjectErasureRegistry(_store.Factory).IsErasedAsync(Tenant, Alice));
        Assert.Empty(await DurableErasedAuditsAsync(_store));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private SubjectErasureService Service() => new(
        new NodeEfSubjectErasureRegistry(_store.Factory),
        new NodeEfSubjectTombstoneStore(_store.Factory),
        _trail,
        new Ed25519Signer(KeyPair.Generate()),
        new NoopTenantKeyDestroyer(),
        new FixedClock(),
        minimumWindow: TimeSpan.Zero,
        propagators: [_propagator]);

    private SubjectErasureService DurableService(SearchTestStore store) => new(
        new NodeEfSubjectErasureRegistry(store.Factory),
        new NodeEfSubjectTombstoneStore(store.Factory),
        new NodeAuditTrailStore(store.Factory),
        new Ed25519Signer(KeyPair.Generate()),
        new NoopTenantKeyDestroyer(),
        new FixedClock(),
        minimumWindow: TimeSpan.Zero,
        propagators: [_propagator]);

    private static async Task<List<AuditRecord>> DurableErasedAuditsAsync(SearchTestStore store)
    {
        var records = new List<AuditRecord>();
        await foreach (var record in new NodeAuditTrailStore(store.Factory).QueryAsync(new AuditQuery(Tenant, new AuditEventType("SubjectErased"))))
            records.Add(record);
        return records;
    }

    private static SubjectErasureRequest Request() =>
        new(Tenant, Alice, Now, [new ActorId("captain"), new ActorId("officer")], "erasure-ticket");

    private async Task<int> TombstonesAsync()
    {
        await using var db = _store.CreateContext();
        return await db.SubjectTombstones.CountAsync();
    }

    private async Task<List<AuditRecord>> ErasedAuditsAsync()
    {
        var records = new List<AuditRecord>();
        await foreach (var record in _trail.QueryAsync(new AuditQuery(Tenant, new AuditEventType("SubjectErased"))))
            records.Add(record);
        return records;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var db = _store.CreateContext();
        await db.Database.ExecuteSqlRawAsync(sql);
    }

    private sealed class FixedClock : IRecoveryClock
    {
        public DateTimeOffset UtcNow() => Now;
    }

    private sealed class NoopTenantKeyDestroyer : ITenantKeyDestroyer
    {
        public Task DestroySubjectKeysAsync(TenantId tenant, SubjectId subject, CancellationToken ct) => Task.CompletedTask;

        public Task DestroyTenantKeysAsync(TenantId tenant, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class CountingPropagator : ISubjectErasurePropagator
    {
        public bool FailNext { get; set; }

        public int Calls { get; private set; }

        public Task PropagateErasureAsync(TenantId tenant, SubjectId subject, CancellationToken ct)
        {
            if (FailNext)
            {
                FailNext = false;
                throw new InvalidOperationException("ck-6 injected purge fault");
            }

            Calls++;
            return Task.CompletedTask;
        }
    }

    private sealed class FlakyTrail : IAuditTrail
    {
        private readonly InMemoryAuditTrail _inner = new();

        public bool FailNextAppend { get; set; }

        public ValueTask AppendAsync(AuditRecord record, CancellationToken ct = default)
        {
            if (FailNextAppend)
            {
                FailNextAppend = false;
                throw new InvalidOperationException("ck-6 injected trail fault");
            }

            return _inner.AppendAsync(record, ct);
        }

        public IAsyncEnumerable<AuditRecord> QueryAsync(AuditQuery query, CancellationToken ct = default) =>
            _inner.QueryAsync(query, ct);
    }
}
