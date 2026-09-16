using Harborline.Api.Foundation.Definitions;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

public sealed class PackProjectionTransactionTests
{
    [Fact]
    public async Task Committed_cleanup_failure_is_diagnostic_and_observers_run_once_outside_the_lease()
    {
        var count = 0;
        var transaction = new PackProjectionTransaction();
        transaction.Finally(() => throw new IOException("cleanup failed"));
        transaction.AfterCommit(() => { count++; return Task.CompletedTask; });
        transaction.Commit();
        transaction.Dispose(); // Must not report a refused activation after the commit boundary.
        await Assert.ThrowsAsync<AggregateException>(transaction.ReactAsync);
        await transaction.ReactAsync();
        Assert.Equal(1, count);
        // A fresh activation proves the failed cleanup still released the publication lease.
        using var next = new PackProjectionTransaction();
    }

    [Fact]
    public void Refusal_restores_original_references_and_enlists_shared_store_once()
    {
        var store = new SnapshotStore();
        var before = store.Values;
        using (var transaction = new PackProjectionTransaction())
        {
            transaction.Enlist(store);
            transaction.Enlist(store);
            store.Values.Add("new");
            Assert.NotSame(before, store.Values);
            Assert.Equal(["old"], before);
        }
        Assert.Same(before, store.Values);
        Assert.Equal(1, store.Preparations);
    }

    [Fact]
    public void Durable_commit_failure_discards_every_prepared_store()
    {
        var first = new SnapshotStore();
        var second = new SnapshotStore();
        var firstBefore = first.Values;
        var secondBefore = second.Values;
        var durable = new DurableUnit(fail: true);
        using (var transaction = new PackProjectionTransaction())
        {
            transaction.Enlist(first);
            transaction.Enlist(second);
            transaction.Durable(() => durable);
            first.Values.Add("new-first");
            second.Values.Add("new-second");
            Assert.Throws<IOException>(transaction.Commit);
        }
        Assert.Same(firstBefore, first.Values);
        Assert.Same(secondBefore, second.Values);
        Assert.True(durable.Disposed);
        Assert.False(durable.Committed);
    }

    [Fact]
    public async Task Concurrent_reader_observes_complete_committed_projection()
    {
        var first = new SnapshotStore();
        var second = new SnapshotStore();
        Task<string[]> read;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var transaction = new PackProjectionTransaction())
        {
            transaction.Enlist(first);
            transaction.Enlist(second);
            first.Values.Add("new-first");
            using (ExecutionContext.SuppressFlow())
                read = Task.Run(() =>
                {
                    started.SetResult();
                    using var lease = PackProjectionActivationBarrier.Read();
                    return first.Values.Concat(second.Values).ToArray();
                });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(read.IsCompleted);
            second.Values.Add("new-second");
            transaction.Commit();
            Assert.False(read.IsCompleted);
        }
        Assert.Equal(["old", "new-first", "old", "new-second"], await read.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Concurrent_reader_observes_original_projection_after_exception()
    {
        var store = new SnapshotStore();
        Task<string[]> read;
        using (var transaction = new PackProjectionTransaction())
        {
            transaction.Enlist(store);
            store.Values.Add("discard");
            using (ExecutionContext.SuppressFlow())
                read = Task.Run(() =>
                {
                    using var lease = PackProjectionActivationBarrier.Read();
                    return store.Values.ToArray();
                });
        }
        Assert.Equal(["old"], await read.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Admission_reads_are_reentrant_across_await()
    {
        using var transaction = new PackProjectionTransaction();
        await Task.Yield();
        using var lease = PackProjectionActivationBarrier.Read();
        transaction.Commit();
    }

    [Fact]
    public void Unenlisted_store_and_second_durable_unit_fail_closed()
    {
        using var transaction = new PackProjectionTransaction();
        Assert.Throws<InvalidOperationException>(() => transaction.Enlist(new object()));
        var durable = transaction.Durable(() => new DurableUnit(false));
        Assert.Same(durable, transaction.Durable(() => new DurableUnit(false)));
        Assert.Throws<InvalidOperationException>(() => transaction.Durable(() => new OtherDurableUnit()));
    }

    [Fact]
    public async Task Cancellation_waiting_for_activation_does_not_acquire_or_leak_a_lease()
    {
        Task waiting;
        using (PackProjectionActivationBarrier.Read())
        {
            using var cancellation = new CancellationTokenSource();
            using (ExecutionContext.SuppressFlow())
                waiting = Task.Run(() =>
                {
                    using var transaction = new PackProjectionTransaction(cancellation.Token);
                });
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        }
        using var next = new PackProjectionTransaction();
        next.Commit();
    }

    private sealed class SnapshotStore : IPackProjectionParticipant
    {
        internal List<string> Values = ["old"];
        internal int Preparations;
        public void StageProjection(PackProjectionTransaction transaction) => transaction.Stage(this, () =>
        {
            var before = Values;
            var next = new List<string>(before);
            Preparations++;
            Values = next;
            return () => Values = before;
        });
    }

    private sealed class DurableUnit(bool fail) : IPackProjectionDurableUnit
    {
        internal bool Committed;
        internal bool Disposed;
        public void Commit()
        {
            if (fail) throw new IOException("Injected commit failure.");
            Committed = true;
        }
        public void Dispose() => Disposed = true;
    }

    private sealed class OtherDurableUnit : IPackProjectionDurableUnit
    {
        public void Commit() { }
        public void Dispose() { }
    }
}
