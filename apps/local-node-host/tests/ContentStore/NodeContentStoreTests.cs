using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Audit;
using Harborline.Api.LocalNodeHost.Data.ContentStore;
using Harborline.Api.LocalNodeHost.Data.Definitions;
using Harborline.Api.LocalNodeHost.Data.HomeEpoch;
using Harborline.Foundation.Assets.Common;
using Harborline.Foundation.ContentStore;
using Harborline.Kernel.Core;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32.SafeHandles;
using Xunit;
using Xunit.Abstractions;

namespace Harborline.Api.LocalNodeHost.Tests.ContentStore;

// T-521/content-store-bookkeeping-in-node-model. Names carry the slice's acceptance clause. Expected values are literals or the
// operating system's own view of the files: the content id in a path, the bytes of a key, a row count.
[Collection("Node content store")]
public sealed partial class NodeContentStoreTests(ITestOutputHelper output) : IDisposable
{
    private static readonly TenantId Tenant = new("test-tenant");
    private static readonly TimeSpan Expiry = TimeSpan.FromMinutes(1);
    private static readonly ContentReadContext Reader = new("trace", "operation", "tenant-opaque", "principal.reader");
    private readonly NodeContentTestStore _node = NodeContentTestStore.Create();
    private readonly TestClock _clock = new();

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class Keys : ITenantContentKeyProvider
    {
        public byte[] GetKey(TenantId tenant) => SHA256.HashData("fixed-test-root"u8);
    }

    private InMemoryContentStore Store(long itemLimit = 300_000, long recordLimit = 600_000, long tenantLimit = 600_000, IContentStoreStorage? storage = null) =>
        new(new(itemLimit, recordLimit, tenantLimit, Expiry), new Keys(), _clock, storage ?? _node.Storage);

    private static ContentStoreWrite Write(string actor = "principal.writer", IContentBookkeepingSession? transaction = null)
    {
        var id = Guid.NewGuid().ToString("N");
        return new(new("command-" + id, "key-" + id, "fingerprint-" + id), new("audit-" + id, actor, DateTimeOffset.UnixEpoch, ReadOnlyMemory<byte>.Empty), transaction);
    }

    private static async Task<Guid> Put(InMemoryContentStore store, byte[]? bytes = null) =>
        await store.PutAsync(Tenant, new MemoryStream(bytes ?? "hello"u8.ToArray()), "text/plain", null, Write());

    private static async Task<ContentReference> Commit(InMemoryContentStore store, Guid stage, string record = "record", string element = "one", IContentBookkeepingSession? transaction = null) =>
        await store.CommitReferenceAsync(Tenant, stage, new(record, element), "x.txt", Write(transaction: transaction));

    private static ValueTask<int> Sweep(InMemoryContentStore store) => store.SweepExpiredAsync("sweep", Write(ContentStoreRunRegistration.StagingSweepActor));

    private static byte[] Text(int length, char fill = 'a') => Enumerable.Repeat((byte)fill, length).ToArray();

    private string[] ContentFiles() => Directory.EnumerateFiles(_node.Storage.DirectoryPath, "*", SearchOption.AllDirectories).ToArray();

    private async Task<long> Quota(bool tenantGrain, string record = "")
    {
        await using var context = await _node.Contexts.CreateDbContextAsync();
        return await context.Set<ContentQuotaRow>().Where(row => row.TenantId == Tenant.Value && row.TenantGrain == tenantGrain && row.Record == record).Select(row => row.Bytes).FirstOrDefaultAsync();
    }

    private static long Scalar(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static async Task<string?> Outcome(Task<ContentReference> commit)
    {
        try
        {
            await commit;
            return null;
        }
        catch (ContentRefusedException refusal)
        {
            return refusal.Code;
        }
    }

    [Fact]
    public async Task Clause_1_the_migration_creates_the_bookkeeping_tables_in_local_node_db()
    {
        await using var context = await _node.Contexts.CreateDbContextAsync();
        Assert.Contains("20261106000000_AddContentStoreBookkeeping", await context.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        Assert.Equal(0, await context.Set<ContentItemRow>().CountAsync());
        Assert.Equal(0, await context.Set<ContentStagedRow>().CountAsync());
        Assert.Equal(0, await context.Set<ContentOwnerRow>().CountAsync());
        Assert.Equal(0, await context.Set<ContentQuotaRow>().CountAsync());
        Assert.Equal(0, await context.Set<ContentReclamationRow>().CountAsync());
        Assert.Equal(0, await context.Set<ContentIntegrityRow>().CountAsync());
        Assert.Equal(0, await context.Set<ContentOperationRow>().CountAsync());
    }

    [Fact]
    public async Task Clause_1_every_node_connection_waits_thirty_seconds_and_overwrites_what_it_deletes()
    {
        await using var context = await _node.Contexts.CreateDbContextAsync();
        await context.Database.OpenConnectionAsync();
        var connection = context.Database.GetDbConnection();
        Assert.Equal(30_000L, Scalar(connection, "PRAGMA busy_timeout;"));
        Assert.Equal(1L, Scalar(connection, "PRAGMA secure_delete;"));
    }

    [Fact]
    public async Task Clause_1_a_second_writer_opening_begin_immediate_waits_and_then_commits_with_no_busy_error()
    {
        await using var first = await _node.Contexts.CreateDbContextAsync();
        var held = await HomeEpochFenceTransaction.BeginAsync(first);
        first.Set<DefinitionArchiveMark>().Add(new() { TenantId = "first", Kind = 0, DefinitionKey = "first" });
        await first.SaveChangesAsync();

        var entered = new TaskCompletionSource();
        var second = Task.Run(async () =>
        {
            await using var context = await _node.Contexts.CreateDbContextAsync();
            entered.SetResult();
            await using var fence = await HomeEpochFenceTransaction.BeginAsync(context);
            context.Set<DefinitionArchiveMark>().Add(new() { TenantId = "second", Kind = 0, DefinitionKey = "second" });
            await context.SaveChangesAsync();
            await fence.CommitAsync();
        });
        await entered.Task;
        await Task.Delay(250);
        Assert.False(second.IsCompleted);

        await held.CommitAsync();
        await held.DisposeAsync();
        await second;
        await using var read = await _node.Contexts.CreateDbContextAsync();
        Assert.Equal(2, await read.Set<DefinitionArchiveMark>().CountAsync());
    }

    [Fact]
    public async Task Clause_1_past_both_lowered_limits_the_second_writer_fails_busy_and_commits_nothing()
    {
        await using var first = await _node.Contexts.CreateDbContextAsync();
        await using var held = await HomeEpochFenceTransaction.BeginAsync(first);

        await using var context = await _node.Contexts.CreateDbContextAsync();
        await context.Database.OpenConnectionAsync();
        var connection = (SqliteConnection)context.Database.GetDbConnection();
        connection.DefaultTimeout = 1;
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA busy_timeout = 100;";
            pragma.ExecuteNonQuery();
        }

        var busy = await Assert.ThrowsAsync<SqliteException>(() => HomeEpochFenceTransaction.BeginAsync(context));
        Assert.Equal(5, busy.SqliteErrorCode); // SQLITE_BUSY
        await held.RollbackAsync();
        Assert.Equal(0, await context.Set<DefinitionArchiveMark>().CountAsync());
    }

    [Fact]
    public async Task Clause_1_two_concurrent_commits_that_each_fit_alone_leave_exactly_one_at_record_grain()
    {
        var store = Store(recordLimit: 5);
        var stages = new[] { await Put(store, Text(5, 'a')), await Put(store, Text(5, 'b')) };
        var outcomes = await Task.WhenAll(stages.Select(stage => Outcome(Commit(store, stage))));
        Assert.Equal(1, outcomes.Count(code => code is null));
        Assert.Equal(1, outcomes.Count(code => code == "content.record-quota"));
        Assert.Equal(5, await Quota(false, "record"));
        Assert.Equal(5, await Quota(true));
    }

    [Fact]
    public async Task Clause_1_two_concurrent_commits_that_each_fit_alone_leave_exactly_one_at_tenant_grain()
    {
        var store = Store(tenantLimit: 5);
        var stages = new[] { await Put(store, Text(5, 'a')), await Put(store, Text(5, 'b')) };
        var outcomes = await Task.WhenAll(stages.Select((stage, index) => Outcome(Commit(store, stage, "record-" + index))));
        Assert.Equal(1, outcomes.Count(code => code is null));
        Assert.Equal(5, await Quota(true));
        Assert.Equal(5, await Quota(false, "record-0") + await Quota(false, "record-1"));
    }

    [Fact]
    public async Task Clause_1_the_same_bytes_committed_concurrently_converge_on_one_item_with_both_references()
    {
        var store = Store();
        var stages = new[] { await Put(store), await Put(store) };
        var references = await Task.WhenAll(stages.Select((stage, index) => Commit(store, stage, "record-" + index)));
        Assert.Equal(references[0].ContentId, references[1].ContentId);
        Assert.Equal(1, _node.Storage.Bookkeeping.CommittedCount);
        var item = await _node.Storage.Bookkeeping.FindItemAsync(new(Tenant, references[0].ContentId));
        Assert.Equal(2, item!.Owners.Count);
    }

    [Fact]
    public async Task Clause_1_a_total_equal_to_the_limit_fits_and_one_byte_more_refuses_changing_neither_count()
    {
        await using var opened = await _node.Storage.Bookkeeping.BeginTransactionAsync();
        IKernelPreparedTransaction<ContentStoreOperation, bool> transaction = opened;
        Assert.Null((await opened.TryReserveQuotaAsync(Tenant, "record", 5, 5, 8)).Refused);
        var record = await opened.TryReserveQuotaAsync(Tenant, "record", 1, 5, 8);
        Assert.Equal(ContentQuotaGrain.Record, record.Refused);
        Assert.Equal(6, record.Observed);
        var tenant = await opened.TryReserveQuotaAsync(Tenant, "other", 4, 5, 8);
        Assert.Equal(ContentQuotaGrain.Tenant, tenant.Refused);
        Assert.Equal(9, tenant.Observed);
        await transaction.StageOperationAsync(new("c", "k", "f"));
        await transaction.StageRecordAsync(ContentStoreOperation.Commit);
        await transaction.StageAuditAsync(new("a", "principal.writer", DateTimeOffset.UnixEpoch, ReadOnlyMemory<byte>.Empty));
        await transaction.CommitAsync();
        Assert.Equal(5, await Quota(false, "record"));
        Assert.Equal(0, await Quota(false, "other"));
        Assert.Equal(5, await Quota(true));
    }

    [Fact]
    public async Task Clause_1_a_repeated_idempotency_key_is_refused_and_a_rolled_back_one_is_free_again()
    {
        var operation = new KernelOperationIdentity("c", "k", "f");
        await using (var opened = await _node.Storage.Bookkeeping.BeginTransactionAsync())
        {
            IKernelPreparedTransaction<ContentStoreOperation, bool> rolledBack = opened;
            await rolledBack.StageOperationAsync(operation);
            await rolledBack.RollbackAsync();
        }

        await using (var opened = await _node.Storage.Bookkeeping.BeginTransactionAsync())
        {
            IKernelPreparedTransaction<ContentStoreOperation, bool> committed = opened;
            await committed.StageOperationAsync(operation);
            await committed.CommitAsync();
        }

        await using var again = await _node.Storage.Bookkeeping.BeginTransactionAsync();
        IKernelPreparedTransaction<ContentStoreOperation, bool> repeated = again;
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await repeated.StageOperationAsync(operation));
    }

    [Fact]
    public async Task Clause_1_removing_a_staged_entry_and_the_name_check_touch_only_the_named_item()
    {
        var store = Store();
        var removed = await Put(store, Text(2, 'a'));
        var kept = await Put(store, Text(2, 'b'));
        var bookkeeping = _node.Storage.Bookkeeping;
        Assert.True(await bookkeeping.NamesBytesAsync(removed));
        Assert.False(await bookkeeping.NamesBytesAsync(Guid.NewGuid()));

        await using (var opened = await bookkeeping.BeginTransactionAsync())
        {
            IKernelPreparedTransaction<ContentStoreOperation, bool> transaction = opened;
            Assert.True(await opened.RemoveStagedAsync(removed));
            Assert.False(await opened.RemoveStagedAsync(removed));
            await transaction.CommitAsync();
        }

        Assert.Equal(1, bookkeeping.StagedCount);
        Assert.False(await bookkeeping.NamesBytesAsync(removed));
        Assert.True(await bookkeeping.NamesBytesAsync(kept));
        await Commit(store, kept);
        Assert.Equal(0, bookkeeping.StagedCount);
        Assert.True(await bookkeeping.NamesBytesAsync(kept));
    }

    [Fact]
    public async Task Clause_1_removing_a_repeated_owner_takes_one_occurrence_and_keeps_the_order()
    {
        var store = Store();
        var first = await Commit(store, await Put(store), "record", "x");
        await Commit(store, await Put(store), "record", "y");
        await Commit(store, await Put(store), "record", "x");
        await store.RemoveReferenceAsync(Tenant, first, HoldStatus.None, "disposal", Write());
        var item = await _node.Storage.Bookkeeping.FindItemAsync(new(Tenant, first.ContentId));
        Assert.Equal([new ContentOwner("record", "y"), new ContentOwner("record", "x")], item!.Owners);
    }

    [Fact]
    public async Task Clause_2_two_joined_writes_for_two_tenants_each_leave_one_row_in_their_own_chain()
    {
        var store = Store();
        var other = new TenantId("other-tenant");
        await using (var context = await _node.Contexts.CreateDbContextAsync())
        {
            await KernelTransactionBoundary.ExecutePreparedAsync<ContentStoreOperation, bool>(async _ =>
            {
                var session = NodeContentBookkeeping.Join(context);
                await store.PutAsync(Tenant, new MemoryStream(Text(2, 'a')), "text/plain", null, Write(transaction: session));
                await store.PutAsync(other, new MemoryStream(Text(2, 'b')), "text/plain", null, Write(transaction: session));
                return CallerCommand(ContentStoreOperation.Put);
            }, new CallerPort(context));
        }

        await using var read = await _node.Contexts.CreateDbContextAsync();
        var rows = await read.Set<NodeAuditEventRow>().AsNoTracking().Where(row => row.EventType == "ContentStore.Put").ToListAsync();
        Assert.Equal(["other-tenant", "test-tenant"], rows.Select(row => row.TenantId).Order(StringComparer.Ordinal));
    }

    private sealed class CallerPort(LocalNodeDbContext context) : IKernelPreparedTransactionPort<ContentStoreOperation, bool>
    {
        public async ValueTask<IKernelPreparedTransaction<ContentStoreOperation, bool>> BeginAsync(CancellationToken cancellationToken = default) =>
            new CallerTransaction(context, await HomeEpochFenceTransaction.BeginAsync(context, cancellationToken));
    }

    // The caller's own record write, standing in for a records write: a row and one commit point.
    private sealed class CallerTransaction(LocalNodeDbContext context, HomeEpochFenceTransaction.Held fence) : IKernelPreparedTransaction<ContentStoreOperation, bool>
    {
        public ValueTask StageOperationAsync(KernelOperationIdentity operation, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask StageRecordAsync(ContentStoreOperation record, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask StageAuditAsync(KernelAuditEvidence audit, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public async ValueTask<bool> CommitAsync(CancellationToken cancellationToken = default)
        {
            await context.SaveChangesAsync(cancellationToken);
            await fence.CommitAsync(cancellationToken);
            return true;
        }

        public ValueTask RollbackAsync(CancellationToken cancellationToken = default) => new(fence.RollbackAsync(cancellationToken));
        public ValueTask DisposeAsync() => fence.DisposeAsync();
    }

    private static KernelCommand<ContentStoreOperation> CallerCommand(ContentStoreOperation operation)
    {
        var id = Guid.NewGuid().ToString("N");
        return new(new(id, id, id), operation, new(id, "principal.writer", DateTimeOffset.UnixEpoch, ReadOnlyMemory<byte>.Empty));
    }

    private static async Task WriteRecordWithReference(InMemoryContentStore store, Guid stage, LocalNodeDbContext context, bool fault)
    {
        await KernelTransactionBoundary.ExecutePreparedAsync<ContentStoreOperation, bool>(async _ =>
        {
            context.Set<DefinitionArchiveMark>().Add(new() { TenantId = Tenant.Value, Kind = 0, DefinitionKey = "stand-in-record" });
            await Commit(store, stage, transaction: NodeContentBookkeeping.Join(context));
            if (fault) throw new IOException("fault before commit");
            return CallerCommand(ContentStoreOperation.Commit);
        }, new CallerPort(context));
    }

    [Fact]
    public async Task Clause_2_a_fault_before_the_callers_commit_leaves_neither_the_record_nor_the_reference()
    {
        var store = Store();
        var stage = await Put(store);
        await using (var context = await _node.Contexts.CreateDbContextAsync())
            await Assert.ThrowsAsync<IOException>(() => WriteRecordWithReference(store, stage, context, fault: true));

        await using var read = await _node.Contexts.CreateDbContextAsync();
        Assert.Equal(0, await read.Set<DefinitionArchiveMark>().CountAsync());
        Assert.Equal(0, _node.Storage.Bookkeeping.CommittedCount);
        Assert.Equal(1, _node.Storage.Bookkeeping.StagedCount);
        Assert.Equal(0, await Quota(true));
        Assert.False(await read.Set<NodeAuditEventRow>().AnyAsync(row => row.EventType == "ContentStore.Commit"));
    }

    [Fact]
    public async Task Clause_2_without_the_fault_the_record_and_the_reference_both_survive_a_restart()
    {
        var store = Store();
        var stage = await Put(store);
        await using (var context = await _node.Contexts.CreateDbContextAsync())
            await WriteRecordWithReference(store, stage, context, fault: false);

        var restarted = _node.Restart();
        await using var read = await restarted.Contexts.CreateDbContextAsync();
        Assert.Equal(1, await read.Set<DefinitionArchiveMark>().CountAsync());
        Assert.Equal(1, restarted.Storage.Bookkeeping.CommittedCount);
        Assert.Equal(0, restarted.Storage.Bookkeeping.StagedCount);
        Assert.Equal(5, await read.Set<ContentQuotaRow>().Where(row => row.TenantGrain).Select(row => row.Bytes).SingleAsync());
    }

    [Fact]
    public async Task Clause_2_a_session_refuses_a_context_with_no_open_transaction()
    {
        await using var context = await _node.Contexts.CreateDbContextAsync();
        Assert.Throws<InvalidOperationException>(() => NodeContentBookkeeping.Join(context));
    }

    [Fact]
    public async Task Clause_3_a_file_is_named_by_the_opaque_key_in_two_character_subdirectories()
    {
        var store = Store();
        var stage = await Put(store);
        var reference = await Commit(store, stage);
        var name = stage.ToString("N", CultureInfo.InvariantCulture);
        Assert.Equal(Path.Combine(_node.DataDirectory, "content", name[..2], name), Assert.Single(ContentFiles()));
        Assert.DoesNotContain(reference.ContentId.Value, Assert.Single(ContentFiles()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clause_3_the_wrapped_key_nonce_and_tag_are_in_the_row_and_never_in_the_file()
    {
        var store = Store();
        var reference = await Commit(store, await Put(store));
        var item = await _node.Storage.Bookkeeping.FindItemAsync(new(Tenant, reference.ContentId));
        var file = await File.ReadAllBytesAsync(Assert.Single(ContentFiles()));
        Assert.Equal(32, item!.WrappedDataKey.Length);
        Assert.Equal(-1, file.AsSpan().IndexOf(item.WrappedDataKey));
        Assert.Equal(-1, file.AsSpan().IndexOf(item.WrapNonce));
        Assert.Equal(-1, file.AsSpan().IndexOf(item.WrapTag));
    }

    [Fact]
    public async Task Clause_3_a_put_writes_a_temporary_file_and_only_a_completed_write_has_its_name()
    {
        var key = Guid.NewGuid();
        var name = key.ToString("N", CultureInfo.InvariantCulture);
        var final = Path.Combine(_node.Storage.DirectoryPath, name[..2], name);
        var writer = await _node.Storage.Bytes.OpenWriteAsync(key, _clock.GetUtcNow());
        await writer.AppendAsync(new([1, 2, 3], new byte[16]));
        Assert.False(File.Exists(final));
        Assert.True(File.Exists(final + ".tmp"));
        Assert.Null(await _node.Storage.Bytes.ReadSegmentAsync(key, 0));

        await writer.CompleteAsync(_clock.GetUtcNow());
        await writer.DisposeAsync();
        Assert.True(File.Exists(final));
        Assert.False(File.Exists(final + ".tmp"));
        Assert.Equal([1, 2, 3], (await _node.Storage.Bytes.ReadSegmentAsync(key, 0))!.Ciphertext);
    }

    [Fact]
    public async Task Clause_3_bytes_are_write_once_and_only_the_last_segment_may_be_short()
    {
        var key = Guid.NewGuid();
        await using (var writer = await _node.Storage.Bytes.OpenWriteAsync(key, _clock.GetUtcNow()))
        {
            await writer.AppendAsync(new(new byte[8], new byte[16]));
            await writer.AppendAsync(new(new byte[4], new byte[16]));
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await writer.AppendAsync(new(new byte[2], new byte[16])));
            await writer.CompleteAsync(_clock.GetUtcNow());
        }

        await Assert.ThrowsAsync<IOException>(async () => await _node.Storage.Bytes.OpenWriteAsync(key, _clock.GetUtcNow()));
        Assert.Equal(8, (await _node.Storage.Bytes.ReadSegmentAsync(key, 0))!.Ciphertext.Length);
        Assert.Equal(4, (await _node.Storage.Bytes.ReadSegmentAsync(key, 1))!.Ciphertext.Length);
        Assert.Null(await _node.Storage.Bytes.ReadSegmentAsync(key, 2));
        Assert.Null(await _node.Storage.Bytes.ReadSegmentAsync(key, -1));
    }

    [Fact]
    public async Task Clause_3_a_truncated_or_foreign_file_has_no_segment()
    {
        var key = Guid.NewGuid();
        await using (var writer = await _node.Storage.Bytes.OpenWriteAsync(key, _clock.GetUtcNow()))
        {
            await writer.AppendAsync(new(new byte[8], new byte[16]));
            await writer.CompleteAsync(_clock.GetUtcNow());
        }

        var path = Assert.Single(ContentFiles());
        var bytes = await File.ReadAllBytesAsync(path);
        await File.WriteAllBytesAsync(path, bytes[..^1]);
        Assert.Null(await _node.Storage.Bytes.ReadSegmentAsync(key, 0));
        var foreign = bytes.ToArray();
        foreign[0] ^= 1;
        await File.WriteAllBytesAsync(path, foreign);
        Assert.Null(await _node.Storage.Bytes.ReadSegmentAsync(key, 0));
        var oversized = bytes.ToArray();
        BitConverter.TryWriteBytes(oversized.AsSpan(8), int.MaxValue);
        await File.WriteAllBytesAsync(path, oversized);
        Assert.Null(await _node.Storage.Bytes.ReadSegmentAsync(key, 0));
    }

    [Fact]
    public async Task Clause_3_a_crash_between_the_byte_write_and_the_staged_entry_leaves_bytes_the_sweep_reclaims_only_after_the_grace_period()
    {
        var store = Store();
        var key = Guid.NewGuid();
        await using (var writer = await _node.Storage.Bytes.OpenWriteAsync(key, _clock.GetUtcNow()))
        {
            await writer.AppendAsync(new([1], new byte[16]));
            await writer.CompleteAsync(_clock.GetUtcNow());
        }

        Assert.Equal(0, _node.Storage.Bookkeeping.StagedCount);
        await Sweep(store);
        Assert.NotNull(await _node.Storage.Bytes.ReadSegmentAsync(key, 0));
        _clock.Advance(Expiry + TimeSpan.FromSeconds(1));
        await Sweep(store);
        Assert.Null(await _node.Storage.Bytes.ReadSegmentAsync(key, 0));
        Assert.Empty(ContentFiles());
        await using var context = await _node.Contexts.CreateDbContextAsync();
        Assert.False(await context.Set<NodeAuditEventRow>().AnyAsync(row => row.EventType == "ContentStore.SweepExpired"));
    }

    [Fact]
    public async Task Clause_3_a_write_that_crashed_before_it_completed_is_still_swept_and_a_swept_write_cannot_complete()
    {
        var store = Store();
        var key = Guid.NewGuid();
        var writer = await _node.Storage.Bytes.OpenWriteAsync(key, _clock.GetUtcNow());
        await writer.AppendAsync(new([1], new byte[16]));
        Assert.Empty(await _node.Storage.Bytes.WrittenAtOrBeforeAsync(_clock.GetUtcNow() - TimeSpan.FromSeconds(1)));
        Assert.Equal(key, Assert.Single(await _node.Storage.Bytes.WrittenAtOrBeforeAsync(_clock.GetUtcNow())));

        _clock.Advance(Expiry + TimeSpan.FromSeconds(1));
        await Sweep(store);
        await Assert.ThrowsAnyAsync<IOException>(async () => await writer.CompleteAsync(_clock.GetUtcNow()));
        await writer.DisposeAsync();
        Assert.Empty(ContentFiles());
    }

    [Fact]
    public async Task Clause_3_a_write_abandoned_without_completing_leaves_no_file()
    {
        await using (var writer = await _node.Storage.Bytes.OpenWriteAsync(Guid.NewGuid(), _clock.GetUtcNow()))
            await writer.AppendAsync(new([1], new byte[16]));
        Assert.Empty(ContentFiles());
    }

    [Fact]
    public async Task Clause_3_the_sweep_never_deletes_bytes_a_committed_entry_names_however_old()
    {
        var store = Store();
        var reference = await Commit(store, await Put(store));
        _clock.Advance(TimeSpan.FromDays(365));
        await Sweep(store);
        await using var body = (await store.ReadAsync(Tenant, reference, true, Reader)).Body;
        var bytes = new byte[5];
        Assert.Equal(5, await body.ReadAtLeastAsync(bytes, 5));
        Assert.Equal("hello"u8.ToArray(), bytes);
    }

    [Fact]
    public async Task Clause_3_the_last_removal_commits_then_deletes_the_file()
    {
        var store = Store();
        var reference = await Commit(store, await Put(store));
        Assert.True(await store.RemoveReferenceAsync(Tenant, reference, HoldStatus.None, "disposal", Write()));
        Assert.Empty(ContentFiles());
        Assert.Equal(0, _node.Storage.Bookkeeping.CommittedCount);
        Assert.Equal(0, await Quota(true));
    }

    [Fact]
    public async Task Clause_3_a_removal_joined_to_a_callers_transaction_leaves_the_file_for_the_sweep()
    {
        var store = Store();
        var reference = await Commit(store, await Put(store));
        await using (var context = await _node.Contexts.CreateDbContextAsync())
        {
            await KernelTransactionBoundary.ExecutePreparedAsync<ContentStoreOperation, bool>(async _ =>
            {
                await store.RemoveReferenceAsync(Tenant, reference, HoldStatus.None, "disposal", Write(transaction: NodeContentBookkeeping.Join(context)));
                return CallerCommand(ContentStoreOperation.RemoveReference);
            }, new CallerPort(context));
        }

        Assert.Single(ContentFiles());
        _clock.Advance(Expiry + TimeSpan.FromSeconds(1));
        await Sweep(store);
        Assert.Empty(ContentFiles());
    }

    [Fact]
    public async Task Clause_3_a_reader_of_a_file_does_not_stop_its_deletion()
    {
        var store = Store();
        var reference = await Commit(store, await Put(store));
        var key = (await _node.Storage.Bookkeeping.FindItemAsync(new(Tenant, reference.ContentId)))!.StageId;
        // The share mode the port's own readers open with.
        await using var reader = new FileStream(Assert.Single(ContentFiles()), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        await _node.Storage.Bytes.DeleteAsync(key);
        Assert.Null(await _node.Storage.Bytes.ReadSegmentAsync(key, 0));
    }

    [Fact]
    public async Task Clause_3_on_windows_a_handle_that_refuses_deletion_does_not_fail_the_committed_removal_and_the_sweep_finishes_it()
    {
        var store = Store();
        var reference = await Commit(store, await Put(store));
        var path = Assert.Single(ContentFiles());
        var holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            Assert.True(await store.RemoveReferenceAsync(Tenant, reference, HoldStatus.None, "disposal", Write()));
            Assert.Equal(0, _node.Storage.Bookkeeping.CommittedCount);
            // POSIX unlinks under an open handle; only Windows can refuse.
            Assert.Equal(OperatingSystem.IsWindows(), File.Exists(path));
        }
        finally
        {
            await holder.DisposeAsync();
        }

        _clock.Advance(Expiry + TimeSpan.FromSeconds(1));
        await Sweep(store);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Clause_3_flush_to_device_requests_f_fullfsync_on_macos_and_only_there()
    {
        var path = Path.Combine(_node.DataDirectory, "flush-probe");
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        stream.WriteByte(1);
        Assert.Equal(OperatingSystem.IsMacOS(), NodeContentByteStore.FlushToDevice(stream));
    }

    [Fact]
    public async Task Clause_4_a_reclaimed_key_leaves_no_copy_in_the_database_file_or_the_wal()
    {
        var directory = Path.Combine(_node.DataDirectory, "plain");
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "plain.db");
        var options = new DbContextOptionsBuilder<LocalNodeDbContext>()
            .UseSqlite($"Data Source={database};Pooling=False")
            .AddInterceptors(new PragmasOnly())
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        IHarborlineEntityModule[] modules = [new ContentStoreEntityModule(), new AuditEventEntityModule()];
        var contexts = new Factory(options, modules);
        await using (var context = contexts.CreateDbContext()) await context.Database.MigrateAsync();

        var store = Store(storage: new NodeContentStoreStorage(contexts, directory));
        var reference = await Commit(store, await Put(store));
        var item = await new NodeContentBookkeeping(contexts).FindItemAsync(new(Tenant, reference.ContentId));

        // The positive control: the method can see the key while the row exists.
        Assert.True(ContainsKey(database, item!.WrappedDataKey));

        Assert.True(await store.RemoveReferenceAsync(Tenant, reference, HoldStatus.None, "disposal", Write()));
        Assert.False(ContainsKey(database, item.WrappedDataKey));
        Assert.Equal(0, new FileInfo(database + "-wal").Exists ? new FileInfo(database + "-wal").Length : 0);
    }

    [Fact]
    public async Task Clause_4_a_checkpoint_a_reader_blocks_reports_it_and_the_next_commit_finishes_the_erasure()
    {
        var directory = Path.Combine(_node.DataDirectory, "plain");
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "plain.db");
        var options = new DbContextOptionsBuilder<LocalNodeDbContext>()
            .UseSqlite($"Data Source={database};Pooling=False")
            // The production 30-second wait, so the bound on the post-commit checkpoint is what keeps this test short.
            .AddInterceptors(new PragmasOnly("PRAGMA busy_timeout = 30000; PRAGMA secure_delete = ON;"))
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        IHarborlineEntityModule[] modules = [new ContentStoreEntityModule(), new AuditEventEntityModule()];
        var contexts = new Factory(options, modules);
        await using (var context = contexts.CreateDbContext()) await context.Database.MigrateAsync();
        var storage = new NodeContentStoreStorage(contexts, directory);
        var bookkeeping = (NodeContentBookkeeping)storage.Bookkeeping;
        var store = Store(storage: storage);
        var reference = await Commit(store, await Put(store));
        var key = (await bookkeeping.FindItemAsync(new(Tenant, reference.ContentId)))!.WrappedDataKey;

        await using (var reader = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            await reader.OpenAsync();
            using var snapshot = reader.BeginTransaction(deferred: true);
            await using (var read = reader.CreateCommand())
            {
                read.CommandText = "SELECT count(*) FROM content_store_items;";
                read.Transaction = snapshot;
                await read.ExecuteScalarAsync();
            }

            var removal = Stopwatch.StartNew();
            Assert.True(await store.RemoveReferenceAsync(Tenant, reference, HoldStatus.None, "disposal", Write()));
            Assert.False(await bookkeeping.CheckpointAsync());
            // Both checkpoints, the commit's and this one, wait about a second each, never the connection's 30.
            Assert.InRange(removal.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(10));
        }

        // Closing the last connection checkpoints by itself, so this one stays open and only the retry can empty the WAL.
        await using var open = new SqliteConnection($"Data Source={database};Pooling=False");
        await open.OpenAsync();
        await using (var attach = open.CreateCommand())
        {
            attach.CommandText = "SELECT count(*) FROM content_store_items;";
            await attach.ExecuteScalarAsync();
        }

        await Put(store);
        Assert.False(ContainsKey(database, key));
        Assert.Equal(0, new FileInfo(database + "-wal").Length);
        Assert.True(await bookkeeping.CheckpointAsync());
    }

    private static bool ContainsKey(string database, byte[] key)
    {
        foreach (var path in new[] { database, database + "-wal" })
        {
            if (!File.Exists(path)) continue;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            if (bytes.AsSpan().IndexOf(key) >= 0) return true;
        }

        return false;
    }

    private sealed class PragmasOnly(string pragmas = SqlCipherConnectionInterceptor.ConnectionPragmas) : DbConnectionInterceptor
    {
        public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        {
            using var command = connection.CreateCommand();
            command.CommandText = pragmas;
            command.ExecuteNonQuery();
        }

        public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = pragmas;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private sealed class Factory(DbContextOptions<LocalNodeDbContext> options, IEnumerable<IHarborlineEntityModule> modules) : IDbContextFactory<LocalNodeDbContext>
    {
        public LocalNodeDbContext CreateDbContext() => new(options, modules);
    }

    [Fact]
    public async Task Clause_6_each_write_names_its_actor_in_its_own_audit_row_in_the_nodes_chain()
    {
        var store = Store();
        var reference = await Commit(store, await Put(store));
        await store.RemoveReferenceAsync(Tenant, reference, HoldStatus.None, "disposal", Write("principal.remover"));

        await using var context = await _node.Contexts.CreateDbContextAsync();
        var rows = await context.Set<NodeAuditEventRow>().AsNoTracking().Where(row => row.TenantId == Tenant.Value).ToListAsync();
        Assert.Equal(
            ["ContentStore.Commit:principal.writer", "ContentStore.Put:principal.writer", "ContentStore.RemoveReference:principal.remover"],
            rows.Select(row => row.EventType + ":" + row.Actor).Order(StringComparer.Ordinal));
        var ordered = rows.OrderBy(row => row.EventType == "ContentStore.Put" ? 0 : row.EventType == "ContentStore.Commit" ? 1 : 2).ToList();
        Assert.Null(ordered[0].PrevHash);
        Assert.Equal(ordered[0].Hash, ordered[1].PrevHash);
        Assert.Equal(ordered[1].Hash, ordered[2].PrevHash);
    }

    [Fact]
    public async Task Clause_6_a_sweep_across_two_tenants_leaves_one_row_in_each_tenants_chain()
    {
        var store = Store();
        var other = new TenantId("other-tenant");
        await Put(store);
        await store.PutAsync(other, new MemoryStream("world"u8.ToArray()), "text/plain", null, Write());
        _clock.Advance(Expiry + TimeSpan.FromSeconds(1));
        Assert.Equal(2, await Sweep(store));

        await using var context = await _node.Contexts.CreateDbContextAsync();
        var rows = await context.Set<NodeAuditEventRow>().AsNoTracking().Where(row => row.EventType == "ContentStore.SweepExpired").ToListAsync();
        Assert.Equal(["other-tenant", "test-tenant"], rows.Select(row => row.TenantId).Order(StringComparer.Ordinal));
        Assert.All(rows, row => Assert.Equal("sys.scheduler", row.Actor));
    }

    [Fact]
    public async Task Clause_6_an_integrity_episodes_audit_row_names_the_reader_and_its_payload_names_no_tenant()
    {
        var store = Store();
        var reference = await Commit(store, await Put(store));
        var path = Assert.Single(ContentFiles());
        var bytes = await File.ReadAllBytesAsync(path);
        bytes[16] ^= 1;
        await File.WriteAllBytesAsync(path, bytes);
        await Assert.ThrowsAnyAsync<CryptographicException>(async () => await store.ReadAsync(Tenant, reference, true, Reader));

        await using var context = await _node.Contexts.CreateDbContextAsync();
        var row = await context.Set<NodeAuditEventRow>().AsNoTracking().SingleAsync(audit => audit.EventType == "ContentStore.IntegrityFailure");
        Assert.Equal("test-tenant", row.TenantId);
        Assert.Equal("principal.reader", row.Actor);
        Assert.DoesNotContain(Tenant.Value, row.Payload, StringComparison.Ordinal);
        Assert.DoesNotContain(reference.ContentId.Value, row.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clause_6_evidence_the_chain_has_no_column_for_is_refused_not_dropped()
    {
        await using var opened = await _node.Storage.Bookkeeping.BeginTransactionAsync();
        IKernelPreparedTransaction<ContentStoreOperation, bool> transaction = opened;
        await transaction.StageRecordAsync(ContentStoreOperation.SweepExpired);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await transaction.StageAuditAsync(
            new("a", "actor", DateTimeOffset.UnixEpoch, ReadOnlyMemory<byte>.Empty) { AuthoritySnapshot = new byte[] { 1 } }));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await transaction.StageAuditAsync(
            new("a", "actor", DateTimeOffset.UnixEpoch, ReadOnlyMemory<byte>.Empty) { OriginatorId = "someone" }));
    }

    [Fact]
    public async Task Clause_6_the_host_creates_no_content_db_and_a_node_that_has_one_starts_and_leaves_it_alone()
    {
        var store = Store();
        await Commit(store, await Put(store));
        Assert.Empty(Directory.EnumerateFiles(_node.DataDirectory, "content.db", SearchOption.AllDirectories));

        var old = Path.Combine(_node.DataDirectory, "content-store");
        Directory.CreateDirectory(old);
        var leftover = Path.Combine(old, "content.db");
        await File.WriteAllBytesAsync(leftover, [1, 2, 3]);
        var services = new ServiceCollection();
        services.AddSingleton(_node.Contexts);
        services.AddNodeContentStore(_node.DataDirectory);
        await using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IContentStoreStorage>();
        Assert.Equal(1, storage.Bookkeeping.CommittedCount);
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(leftover));
    }

    [Fact]
    public async Task Clause_7_the_quota_base_counts_the_files_under_content_staged_ones_included()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_node.Contexts);
        services.AddNodeContentStore(_node.DataDirectory);
        await using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<NodeContentStoreStorage>();
        Assert.Same(storage, provider.GetRequiredService<IContentStoreStorage>());
        var quota = provider.GetRequiredService<IContentStoreQuotaBase>();
        var store = Store(itemLimit: 1 << 24, recordLimit: 1 << 24, tenantLimit: 1 << 24, storage: storage);

        // Text, so the store admits it; every byte is written, so allocation has to count it.
        var bytes = Text(1 << 23);
        await store.PutAsync(Tenant, new MemoryStream(bytes), "text/plain", null, Write());
        var staged = Assert.Single(Directory.EnumerateFiles(storage.DirectoryPath, "*", SearchOption.AllDirectories));
        using (var handle = File.OpenHandle(staged)) Assert.True(NodeContentStoreQuotaBase.AllocatedBytes(handle) >= bytes.Length);
        var free = new DriveInfo(storage.DirectoryPath).AvailableFreeSpace;
        Assert.InRange(quota.GetQuotaBaseBytes() - free, bytes.Length - (1L << 22), bytes.Length + (1L << 24));
    }

    [Fact]
    public async Task Clause_7_a_sparse_logical_gigabyte_is_not_counted_as_a_physical_gigabyte()
    {
        var storage = _node.Storage;
        var quota = new NodeContentStoreQuotaBase(storage);
        await using (var sparse = File.Create(Path.Combine(storage.DirectoryPath, "allocation-probe")))
        {
            MarkSparse(sparse);
            sparse.SetLength(1L << 30);
        }

        var free = new DriveInfo(storage.DirectoryPath).AvailableFreeSpace;
        // Free space can move either way between the two reads, so the tolerance is symmetric (16 MiB).
        Assert.InRange(quota.GetQuotaBaseBytes() - free, -16L * 1024 * 1024, 16L * 1024 * 1024);
    }

    [Fact]
    public void Allocated_bytes_match_the_operating_system_block_count_for_dense_and_sparse_files()
    {
        var directory = _node.DataDirectory;
        // Dense: one byte past 1 MiB, so allocation rounds up to a block and differs from st_size.
        var dense = Path.Combine(directory, "dense.bin");
        var bytes = new byte[(1 << 20) + 1];
        RandomNumberGenerator.Fill(bytes);
        using (var stream = new FileStream(dense, FileMode.Create)) { stream.Write(bytes); stream.Flush(true); }
        // Sparse: one written byte, 64 MiB logical length.
        var sparse = Path.Combine(directory, "sparse.bin");
        using (var stream = new FileStream(sparse, FileMode.Create)) { MarkSparse(stream); stream.WriteByte(1); stream.SetLength(64L << 20); stream.Flush(true); }
        foreach (var file in new[] { dense, sparse })
        {
            long measured;
            using (var handle = File.OpenHandle(file)) measured = NodeContentStoreQuotaBase.AllocatedBytes(handle);
            if (OperatingSystem.IsWindows())
            {
                // GetFileInformationByHandleEx is the documented API; with the sparse marker, the 64 MiB probe allocates
                // at most one 1 MiB run and the dense file at least its written length.
                Assert.InRange(measured, file == dense ? bytes.Length : 1, file == dense ? bytes.Length + (1 << 20) : 1 << 20);
                continue;
            }
            // Independent oracle: stat(1)'s allocated block count, never the struct offsets under test.
            Assert.Equal(StatAllocatedBytes(file), measured);
        }
    }

    private static long StatAllocatedBytes(string file)
    {
        var start = OperatingSystem.IsMacOS()
            ? new ProcessStartInfo("stat", ["-f", "%b 512", file])
            : new ProcessStartInfo("stat", ["-c", "%b %B", file]);
        start.RedirectStandardOutput = true;
        using var process = Process.Start(start)!;
        var fields = process.StandardOutput.ReadToEnd().Trim().Split(' ');
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        return long.Parse(fields[0], CultureInfo.InvariantCulture) * long.Parse(fields[1], CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task Durable_commit_dedup_timing_is_measured_with_interleaved_equal_sized_samples()
    {
        var store = Store();
        var bytes = Text(65_536);
        var duplicate = new List<double>();
        var promotion = new List<double>();
        // Warm up crypto, serializer and database before collecting observations.
        for (var i = 0; i < 60; i++)
        {
            bytes[0] = (byte)('a' + i % 26);
            bytes[1] = (byte)('a' + i / 26);
            var stage = await Put(store, bytes);
            var start = Stopwatch.GetTimestamp();
            var reference = await Commit(store, stage);
            var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            var same = await Put(store, bytes);
            start = Stopwatch.GetTimestamp();
            var repeated = await Commit(store, same, "record", "two");
            var dedupElapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            Assert.Equal(reference.ContentId, repeated.ContentId);
            if (i >= 10) { promotion.Add(elapsed); duplicate.Add(dedupElapsed); }
            await store.RemoveReferenceAsync(Tenant, reference, HoldStatus.None, "cleanup", Write());
            await store.RemoveReferenceAsync(Tenant, repeated, HoldStatus.None, "cleanup", Write());
        }

        promotion.Sort();
        duplicate.Sort();
        output.WriteLine($"Durable commit in local-node.db, 50 samples per path: promotion median={promotion[25]:F3}ms p95={promotion[47]:F3}ms; duplicate median={duplicate[25]:F3}ms p95={duplicate[47]:F3}ms.");
        // Measurement only; DES-0037 gate-host qualification is the separately ruled follow-on.
        Assert.All(promotion.Concat(duplicate), value => Assert.True(value > 0));
    }

    // NTFS allocates clusters on SetLength unless the file is first marked sparse (FSCTL_SET_SPARSE,
    // https://learn.microsoft.com/windows/win32/api/winioctl/ni-winioctl-fsctl_set_sparse). Unix filesystems
    // used here leave unwritten ranges unallocated without a marker.
    private static void MarkSparse(FileStream stream)
    {
        if (!OperatingSystem.IsWindows()) return;
        const uint FsctlSetSparse = 0x000900C4;
        Assert.True(DeviceIoControl(stream.SafeFileHandle, FsctlSetSparse, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero),
            $"FSCTL_SET_SPARSE failed: {Marshal.GetLastPInvokeError()}");
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr inBuffer, uint inSize,
        IntPtr outBuffer, uint outSize, out uint returned, IntPtr overlapped);

    public void Dispose() => _node.Dispose();
}
