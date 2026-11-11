using Harborline.Api.LocalNodeHost.Data.ContentStore;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Audit;
using Harborline.Api.LocalNodeHost.Data.Definitions;
using Harborline.Foundation.ContentStore;
using Harborline.Foundation.ExecutionRuntime;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.ContentStore;

public sealed partial class NodeContentStoreTests
{
    [Fact]
    public void Rotation_run_uses_the_slice_literals_and_the_sweeps_registration_shape()
    {
        var registry = new RunKindRegistry();
        NodeContentKeyRotation.Register(registry);
        Assert.Equal("content-store", registry.Require(new("content-key-rotation")).OwningEngine);
        Assert.Equal("schedule", NodeContentKeyRotation.TriggerKind);
        Assert.Equal("sys.sched.content-key-rotation", NodeContentKeyRotation.Schedule);
        var services = new ServiceCollection();
        services.AddNodeContentStore(_node.DataDirectory);
        using var provider = services.BuildServiceProvider();
        Assert.Equal("content-store", provider.GetRequiredService<RunKindRegistry>().Require(new("content-key-rotation")).OwningEngine);
        Assert.Equal("content-store", provider.GetRequiredService<RunKindRegistry>().Require(new("content-staging-sweep")).OwningEngine);
    }

    [Fact]
    public async Task Rotation_rewraps_multiple_batches_and_waits_thirty_days_across_restart()
    {
        var storage = new NodeContentStoreStorage(_node.Contexts, _node.DataDirectory);
        var store = Store(storage: storage);
        var firstReference = await Commit(store, await Put(store, "first"u8.ToArray()));
        var secondReference = await Commit(store, await Put(store, "second"u8.ToArray()), "second-record");
        var first = (await _node.Storage.Epochs.GetCurrentAsync(Tenant))!.EpochId;
        File.SetLastWriteTimeUtc(Path.Combine(EpochDirectory, first.ToString("N") + ".epoch"), _clock.GetUtcNow().UtcDateTime);
        using var rotation = new NodeContentKeyRotation(storage, new(), _clock);

        _clock.Advance(TimeSpan.FromDays(90) - TimeSpan.FromTicks(1));
        await rotation.RunAsync(Tenant, store, () => Write("sys.scheduler"), batchSize: 1);
        Assert.Equal(first, (await _node.Storage.Epochs.GetCurrentAsync(Tenant))!.EpochId);
        _clock.Advance(TimeSpan.FromTicks(1));
        await rotation.RunAsync(Tenant, store, () => Write("sys.scheduler"), batchSize: 1);

        var current = (await _node.Storage.Epochs.GetCurrentAsync(Tenant))!.EpochId;
        Assert.NotEqual(first, current);
        await using (var context = await _node.Contexts.CreateDbContextAsync())
        {
            Assert.All(await context.Set<ContentItemRow>().ToListAsync(), row => Assert.Equal(current, row.EpochId));
        }
        Assert.NotNull(await _node.Storage.Epochs.GetAsync(Tenant, first));
        using var restarted = new NodeContentKeyRotation(storage, new(), _clock);
        _clock.Advance(TimeSpan.FromDays(30) - TimeSpan.FromTicks(1));
        await restarted.RunAsync(Tenant, store, () => Write("sys.scheduler"));
        Assert.NotNull(await _node.Storage.Epochs.GetAsync(Tenant, first));
        _clock.Advance(TimeSpan.FromTicks(1));
        await restarted.RunAsync(Tenant, store, () => Write("sys.scheduler"));
        Assert.Null(await _node.Storage.Epochs.GetAsync(Tenant, first));
        Assert.Equal("first"u8.ToArray(), await ReadAll(store, firstReference));
        Assert.Equal("second"u8.ToArray(), await ReadAll(store, secondReference));
    }

    [Fact]
    public async Task Backup_handles_gate_both_retirement_and_destruction_and_byte_sweeping()
    {
        var storage = new NodeContentStoreStorage(_node.Contexts, _node.DataDirectory);
        var store = Store(storage: storage);
        var reference = await Commit(store, await Put(store));
        var first = (await _node.Storage.Epochs.GetCurrentAsync(Tenant))!.EpochId;
        await store.RotateEpochAsync(Tenant, Write("sys.scheduler"));
        using var rotation = new NodeContentKeyRotation(storage, new(), _clock);
        using (await storage.Backups.OpenAsync())
        {
            using (await storage.Backups.OpenAsync())
            {
                await rotation.RunAsync(Tenant, store, () => Write("sys.scheduler"));
                await using var context = await _node.Contexts.CreateDbContextAsync();
                Assert.Empty(await context.Set<ContentEpochRow>().ToListAsync());
            }
            Assert.True(await store.RemoveReferenceAsync(Tenant, reference, Harborline.Foundation.ContentStore.HoldStatus.None, "disposal", Write()));
            Assert.NotEmpty(ContentFiles());
            _clock.Advance(TimeSpan.FromMinutes(1));
            await Sweep(store);
            Assert.NotEmpty(ContentFiles());
        }
        await rotation.RunAsync(Tenant, store, () => Write("sys.scheduler"));
        await Sweep(store);
        Assert.Empty(ContentFiles());
        _clock.Advance(TimeSpan.FromDays(30));
        using (await storage.Backups.OpenAsync())
        {
            await rotation.RunAsync(Tenant, store, () => Write("sys.scheduler"));
            Assert.NotNull(await _node.Storage.Epochs.GetAsync(Tenant, first));
        }
        await rotation.RunAsync(Tenant, store, () => Write("sys.scheduler"));
        Assert.Null(await _node.Storage.Epochs.GetAsync(Tenant, first));
    }

    [Fact]
    public async Task An_unfinished_TRUNCATE_checkpoint_cannot_start_the_destruction_wait()
    {
        var (storage, contexts) = await PlainRotationBacking("checkpoint");
        var store = Store(storage: storage);
        await Commit(store, await Put(store));
        var first = (await storage.Epochs.GetCurrentAsync(Tenant))!.EpochId;
        using var rotation = new NodeContentKeyRotation(storage, new(), _clock);
        var pendingPath = Path.Combine(((NodeContentEpochKeyStore)storage.Epochs).TenantDirectory(Tenant), first.ToString("N") + ".pending");
        await using (var reader = new SqliteConnection("Data Source=" + Path.Combine(_node.DataDirectory, "checkpoint", "local-node.db") + ";Pooling=False"))
        {
            await reader.OpenAsync();
            using var readTransaction = reader.BeginTransaction(deferred: true);
            await using var read = reader.CreateCommand();
            read.Transaction = readTransaction;
            read.CommandText = "SELECT count(*) FROM content_store_items;";
            Assert.Equal(1L, await read.ExecuteScalarAsync());
            await store.RotateEpochAsync(Tenant, Write("sys.scheduler"));
            await rotation.RunAsync(Tenant, store, () => Write("sys.scheduler"));
            Assert.False(File.Exists(pendingPath));
            await using var context = contexts.CreateDbContext();
            Assert.Empty(await context.Set<ContentEpochRow>().ToListAsync());
        }
        await rotation.RunAsync(Tenant, store, () => Write("sys.scheduler"));
        Assert.True(File.Exists(pendingPath));
        Assert.NotNull(await storage.Epochs.GetAsync(Tenant, first));
    }

    [Fact]
    public async Task The_checkpoint_after_fencing_old_pins_must_finish_before_the_wait_is_marked()
    {
        var (storage, _) = await PlainRotationBacking("final-checkpoint");
        var store = Store(storage: storage);
        await Commit(store, await Put(store));
        var first = (await storage.Epochs.GetCurrentAsync(Tenant))!.EpochId;
        await store.RotateEpochAsync(Tenant, Write("sys.scheduler"));
        var pendingPath = Path.Combine(((NodeContentEpochKeyStore)storage.Epochs).TenantDirectory(Tenant), first.ToString("N") + ".pending");
        using var reader = new SqliteConnection("Data Source=" + Path.Combine(_node.DataDirectory, "final-checkpoint", "local-node.db") + ";Pooling=False");
        reader.Open();
        using (var attach = reader.CreateCommand())
        {
            attach.CommandText = "SELECT count(*) FROM content_store_items;";
            Assert.Equal(1L, attach.ExecuteScalar());
        }
        SqliteTransaction? snapshot = null;
        NodeContentEpochKeyStore.AfterDurableStep = step =>
        {
            if (step != "retire:fenced") return;
            snapshot = reader.BeginTransaction(deferred: true);
            using var read = reader.CreateCommand();
            read.Transaction = snapshot;
            read.CommandText = "SELECT count(*) FROM content_store_epochs;";
            Assert.Equal(1L, read.ExecuteScalar());
        };
        using var rotation = new NodeContentKeyRotation(storage, new(), _clock);
        try
        {
            await rotation.RunAsync(Tenant, store, () => Write("sys.scheduler"));
            Assert.NotNull(snapshot);
            Assert.False(File.Exists(pendingPath));
            Assert.NotNull(await storage.Epochs.GetAsync(Tenant, first));
        }
        finally
        {
            NodeContentEpochKeyStore.AfterDurableStep = null;
            snapshot?.Dispose();
            reader.Close();
        }
        await rotation.RunAsync(Tenant, store, () => Write("sys.scheduler"));
        Assert.True(File.Exists(pendingPath));
    }

    [Fact]
    public async Task Rotation_refuses_a_non_scheduler_actor_before_changing_the_current_epoch()
    {
        var storage = new NodeContentStoreStorage(_node.Contexts, _node.DataDirectory);
        var store = Store(storage: storage);
        await Commit(store, await Put(store));
        var first = (await storage.Epochs.GetCurrentAsync(Tenant))!.EpochId;
        File.SetLastWriteTimeUtc(Path.Combine(EpochDirectory, first.ToString("N") + ".epoch"), _clock.GetUtcNow().UtcDateTime);
        _clock.Advance(TimeSpan.FromDays(90));
        using var rotation = new NodeContentKeyRotation(storage, new(), _clock);
        await Assert.ThrowsAsync<ArgumentException>(async () => await rotation.RunAsync(Tenant, store, () => Write("principal.not-a-scheduler")));
        Assert.Equal(first, (await storage.Epochs.GetCurrentAsync(Tenant))!.EpochId);
    }

    [Fact]
    public async Task Unreadable_pending_metadata_refuses_instead_of_destroying_a_key()
    {
        var storage = new NodeContentStoreStorage(_node.Contexts, _node.DataDirectory);
        var store = Store(storage: storage);
        await Commit(store, await Put(store));
        var first = (await storage.Epochs.GetCurrentAsync(Tenant))!.EpochId;
        await store.RotateEpochAsync(Tenant, Write("sys.scheduler"));
        using var rotation = new NodeContentKeyRotation(storage, new(), _clock);
        await rotation.RunAsync(Tenant, store, () => Write("sys.scheduler"));
        await File.WriteAllTextAsync(Path.Combine(EpochDirectory, first.ToString("N") + ".pending"), "not-a-timestamp");
        _clock.Advance(TimeSpan.FromDays(30));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await rotation.RunAsync(Tenant, store, () => Write("sys.scheduler")));
        Assert.NotNull(await storage.Epochs.GetAsync(Tenant, first));
    }

    [Fact]
    public async Task A_deleted_items_wrap_left_in_an_unencrypted_database_is_unreadable_after_rotation_and_waiting()
    {
        var databasePath = Path.Combine(_node.DataDirectory, "plaintext", "local-node.db");
        var (storage, _) = await PlainRotationBacking("plaintext");
        var store = Store(storage: storage);
        var reference = await Commit(store, await Put(store));
        var item = (await storage.Bookkeeping.FindItemAsync(new(Tenant, reference.ContentId)))!;
        File.SetLastWriteTimeUtc(Path.Combine(((NodeContentEpochKeyStore)storage.Epochs).TenantDirectory(Tenant), item.EpochId.ToString("N") + ".epoch"), _clock.GetUtcNow().UtcDateTime);
        CryptographicOperations.ZeroMemory(await OpenLeftoverWrap(storage, item));
        await store.RemoveReferenceAsync(Tenant, reference, HoldStatus.None, "disposal", Write());
        Assert.True(await ((NodeContentBookkeeping)storage.Bookkeeping).CheckpointAsync());
        var remainder = await File.ReadAllBytesAsync(databasePath);
        Assert.True(remainder.AsSpan().StartsWith("SQLite format 3\0"u8));
        Assert.True(remainder.AsSpan().IndexOf(item.WrappedDataKey) >= 0);

        using var rotation = new NodeContentKeyRotation(storage, new(), _clock);
        _clock.Advance(TimeSpan.FromDays(90));
        await rotation.RunAsync(Tenant, store, () => Write("sys.scheduler"));
        CryptographicOperations.ZeroMemory(await OpenLeftoverWrap(storage, item));
        _clock.Advance(TimeSpan.FromDays(30));
        await rotation.RunAsync(Tenant, store, () => Write("sys.scheduler"));
        Assert.True((await File.ReadAllBytesAsync(databasePath)).AsSpan().IndexOf(item.WrappedDataKey) >= 0);
        await Assert.ThrowsAsync<CryptographicException>(async () => await OpenLeftoverWrap(storage, item));
    }

    private async Task<(NodeContentStoreStorage Storage, Factory Contexts)> PlainRotationBacking(string name)
    {
        var directory = Path.Combine(_node.DataDirectory, name);
        Directory.CreateDirectory(directory);
        var options = new DbContextOptionsBuilder<LocalNodeDbContext>()
            .UseSqlite("Data Source=" + Path.Combine(directory, "local-node.db") + ";Pooling=False")
            .AddInterceptors(new PragmasOnly("PRAGMA journal_mode=WAL; PRAGMA secure_delete=OFF; PRAGMA busy_timeout=30000;"))
            .Options;
        var contexts = new Factory(options, [new ContentStoreEntityModule(), new AuditEventEntityModule(), new DefinitionArchiveEntityModule()]);
        await using (var context = contexts.CreateDbContext()) await context.Database.EnsureCreatedAsync();
        return (new(contexts, directory), contexts);
    }

    private static async Task<byte[]> OpenLeftoverWrap(NodeContentStoreStorage storage, ContentStoreItem item)
    {
        var epoch = await storage.Epochs.GetAsync(item.Tenant, item.EpochId) ?? throw new CryptographicException("The epoch key no longer exists.");
        var subkey = new byte[32];
        var epochKey = new byte[32];
        var dataKey = new byte[32];
        try
        {
            HKDF.DeriveKey(HashAlgorithmName.SHA256, SHA256.HashData("fixed-test-root"u8), subkey,
                Encoding.UTF8.GetBytes(item.Tenant.Value), "harborline-content-store:data-key-wrap:v1"u8);
            using (var aead = new AesGcm(subkey, 16))
                aead.Decrypt(epoch.WrapNonce, epoch.WrappedKey, epoch.WrapTag, epochKey,
                    Encoding.UTF8.GetBytes("harborline-content-store:epoch-key:v1|" + epoch.EpochId.ToString("N") + "|" + item.Tenant.Value));
            using (var aead = new AesGcm(epochKey, 16))
                aead.Decrypt(item.WrapNonce, item.WrappedDataKey, item.WrapTag, dataKey, Encoding.ASCII.GetBytes(item.ContentId.Value));
            return dataKey;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(subkey);
            CryptographicOperations.ZeroMemory(epochKey);
        }
    }

}
