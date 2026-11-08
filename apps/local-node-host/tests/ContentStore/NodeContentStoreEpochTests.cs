using System.Security.Cryptography;
using System.Text;
using Harborline.Api.LocalNodeHost.Data.ContentStore;
using Harborline.Foundation.Assets.Common;
using Harborline.Foundation.ContentStore;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.ContentStore;

// T-521/content-store-epoch-key-store, stage 1: the node backs the platform's epoch key port (ADR 0116 decisions 1, 4, 5).
// Oracles: the slice record's literals (the directory names, the migration id), ADR 0116's rules (a key file before any row
// names it, no destroy while a row names the epoch, a retired epoch refuses later pins), and the raw database bytes.
public sealed partial class NodeContentStoreTests
{
    private string EpochDirectory => Path.Combine(_node.DataDirectory, "tenant-keys", "content-epochs",
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Tenant.Value))));

    private async Task<Guid> RowEpoch(Guid itemKey)
    {
        await using var context = await _node.Contexts.CreateDbContextAsync();
        return await context.Set<ContentItemRow>().Where(row => row.ItemKey == itemKey).Select(row => row.EpochId).SingleAsync();
    }

    [Fact]
    public async Task Epoch_clause_2_the_migration_adds_the_epoch_column_and_marker_table()
    {
        await using var context = await _node.Contexts.CreateDbContextAsync();
        Assert.Contains("20261108000000_AddContentStoreEpochs", await context.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        Assert.Equal(0, await context.Set<ContentEpochRow>().CountAsync());
    }

    [Fact]
    public async Task Epoch_clause_1_the_epoch_key_is_a_file_in_tenant_keys_and_never_in_local_node_db()
    {
        var store = Store();
        var reference = await Commit(store, await Put(store));
        var item = (await _node.Storage.Bookkeeping.FindItemAsync(new(Tenant, reference.ContentId)))!;
        var epoch = (await _node.Storage.Epochs.GetCurrentAsync(Tenant))!;

        Assert.Equal(epoch.EpochId, item.EpochId);
        Assert.Equal(epoch.EpochId, await RowEpoch(item.StageId));
        Assert.Equal(new[] { epoch.EpochId.ToString("N") + ".epoch", "current" },
            Directory.EnumerateFiles(EpochDirectory).Select(path => Path.GetFileName(path)!).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(epoch.EpochId.ToString("N"), await File.ReadAllTextAsync(Path.Combine(EpochDirectory, "current")));
        await _node.Storage.Bookkeeping.CheckpointAsyncOrSkip();
        var database = await File.ReadAllBytesAsync(_node.DatabasePath);
        Assert.Equal(-1, database.AsSpan().IndexOf(epoch.WrappedKey));
        Assert.True(File.ReadAllBytes(Path.Combine(EpochDirectory, epoch.EpochId.ToString("N") + ".epoch")).AsSpan().IndexOf(epoch.WrappedKey) > 0);
    }

    [Fact]
    public async Task Epoch_clause_3_4_rotate_rewrap_destroy_moves_every_row_retires_the_epoch_and_deletes_its_file()
    {
        var store = Store();
        var committed = await Commit(store, await Put(store, Text(10, 'c')));
        var staged = await Put(store, Text(10, 's'));
        var first = (await _node.Storage.Epochs.GetCurrentAsync(Tenant))!.EpochId;

        var rotation = await store.RotateEpochAsync(Tenant, Write("sys.content-key-rotation"));
        Assert.Equal(first, rotation.From);
        await Assert.ThrowsAsync<ContentEpochInUseException>(async () => await store.DestroyEpochAsync(Tenant, first, Write("sys.content-key-rotation")));
        Assert.Equal(2, await store.RewrapEpochAsync(Tenant, first, rotation.To, 1, Write("sys.content-key-rotation"))
            + await store.RewrapEpochAsync(Tenant, first, rotation.To, 1, Write("sys.content-key-rotation")));
        Assert.True(await store.DestroyEpochAsync(Tenant, first, Write("sys.content-key-rotation")));

        Assert.False(File.Exists(Path.Combine(EpochDirectory, first.ToString("N") + ".epoch")));
        Assert.Null(await _node.Storage.Epochs.GetAsync(Tenant, first));
        Assert.Equal(rotation.To, await RowEpoch((await _node.Storage.Bookkeeping.FindItemAsync(new(Tenant, committed.ContentId)))!.StageId));
        Assert.Equal("cccccccccc"u8.ToArray(), await ReadAll(store, committed));
        Assert.NotNull(await Commit(store, staged));
        await using var context = await _node.Contexts.CreateDbContextAsync();
        Assert.Equal(first, (await context.Set<ContentEpochRow>().SingleAsync()).EpochId);
    }

    [Fact]
    public async Task Epoch_clause_4_a_staged_row_alone_keeps_its_epoch_from_retirement()
    {
        var store = Store();
        await Put(store);
        var first = (await _node.Storage.Epochs.GetCurrentAsync(Tenant))!.EpochId;
        await store.RotateEpochAsync(Tenant, Write("sys.content-key-rotation"));

        await Assert.ThrowsAsync<ContentEpochInUseException>(async () => await store.DestroyEpochAsync(Tenant, first, Write("sys.content-key-rotation")));
        Assert.NotNull(await _node.Storage.Epochs.GetAsync(Tenant, first));
    }

    [Fact]
    public async Task Epoch_clause_4_a_committed_row_alone_keeps_its_epoch_from_retirement()
    {
        var store = Store();
        await Commit(store, await Put(store));
        var first = (await _node.Storage.Epochs.GetCurrentAsync(Tenant))!.EpochId;
        await store.RotateEpochAsync(Tenant, Write("sys.content-key-rotation"));

        await Assert.ThrowsAsync<ContentEpochInUseException>(async () => await store.DestroyEpochAsync(Tenant, first, Write("sys.content-key-rotation")));
        Assert.NotNull(await _node.Storage.Epochs.GetAsync(Tenant, first));
    }

    [Fact]
    public async Task Epoch_clause_4_a_retired_epoch_refuses_every_later_pin_and_a_moved_row_refuses_a_stale_rewrap()
    {
        var store = Store();
        var reference = await Commit(store, await Put(store));
        var item = (await _node.Storage.Bookkeeping.FindItemAsync(new(Tenant, reference.ContentId)))!;
        var first = item.EpochId;
        var rotation = await store.RotateEpochAsync(Tenant, Write("sys.content-key-rotation"));

        await using (var transaction = await _node.Storage.Bookkeeping.BeginTransactionAsync())
        {
            Assert.True(await transaction.PinEpochAsync(Tenant, first));
            Assert.True(await transaction.ReplaceWrapAsync(item, rotation.To, item.WrappedDataKey, item.WrapNonce, item.WrapTag));
            Assert.False(await transaction.ReplaceWrapAsync(item, rotation.To, item.WrappedDataKey, item.WrapNonce, item.WrapTag));
            Assert.True(await transaction.RetireEpochIfUnnamedAsync(Tenant, first));
            Assert.False(await transaction.PinEpochAsync(Tenant, first));
            Assert.True(await transaction.PinEpochAsync(Tenant, rotation.To));
            await transaction.RollbackAsync();
        }

        Assert.Equal(first, await RowEpoch(item.StageId));
    }

    [Fact]
    public async Task Epoch_clause_1_the_current_epoch_survives_a_restart()
    {
        var store = Store();
        await Commit(store, await Put(store));
        var epoch = (await _node.Storage.Epochs.GetCurrentAsync(Tenant))!;

        using var restarted = _node.Restart();
        var again = (await restarted.Storage.Epochs.GetCurrentAsync(Tenant))!;

        Assert.Equal(epoch.EpochId, again.EpochId);
        Assert.Equal(epoch.WrappedKey, again.WrappedKey);
        Assert.Equal(epoch.WrapNonce, again.WrapNonce);
        Assert.Equal(epoch.WrapTag, again.WrapTag);
    }

    [Fact]
    public async Task Epoch_key_store_switches_only_from_the_expected_epoch_and_never_overwrites_a_key()
    {
        using var keys = new NodeContentEpochKeyStore(Path.Combine(_node.DataDirectory, "tenant-keys", NodeContentEpochKeyStore.DirectoryName));
        var one = new ContentEpochKey(Guid.NewGuid(), [1, 2, 3], new byte[12], new byte[16]);
        var two = new ContentEpochKey(Guid.NewGuid(), [4, 5, 6], new byte[12], new byte[16]);
        await keys.CreateAsync(Tenant, one);
        await keys.CreateAsync(Tenant, two);

        Assert.True(await keys.TrySetCurrentAsync(Tenant, null, one.EpochId));
        Assert.False(await keys.TrySetCurrentAsync(Tenant, null, two.EpochId));
        Assert.False(await keys.TrySetCurrentAsync(Tenant, two.EpochId, two.EpochId));
        Assert.Equal(one.EpochId, (await keys.GetCurrentAsync(Tenant))!.EpochId);
        Assert.True(await keys.TrySetCurrentAsync(Tenant, one.EpochId, two.EpochId));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await keys.CreateAsync(Tenant, one with { WrappedKey = [9] }));
        Assert.Equal([1, 2, 3], (await keys.GetAsync(Tenant, one.EpochId))!.WrappedKey);

        await keys.DestroyAsync(Tenant, two.EpochId);
        Assert.Null(await keys.GetCurrentAsync(Tenant));
        Assert.Null(await keys.GetAsync(Tenant, two.EpochId));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(EpochFile(keys, one.EpochId))!, "*.tmp"));
    }

    [Fact]
    public async Task Epoch_key_file_that_is_truncated_or_padded_never_decodes()
    {
        using var keys = new NodeContentEpochKeyStore(Path.Combine(_node.DataDirectory, "tenant-keys", NodeContentEpochKeyStore.DirectoryName));
        var epoch = new ContentEpochKey(Guid.NewGuid(), new byte[32], new byte[12], new byte[16]);
        await keys.CreateAsync(Tenant, epoch);
        var path = EpochFile(keys, epoch.EpochId);
        var bytes = await File.ReadAllBytesAsync(path);

        await File.WriteAllBytesAsync(path, bytes[..^1]);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await keys.GetAsync(Tenant, epoch.EpochId));
        await File.WriteAllBytesAsync(path, [.. bytes, 0]);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await keys.GetAsync(Tenant, epoch.EpochId));
    }

    private string EpochFile(NodeContentEpochKeyStore keys, Guid epochId) =>
        Path.Combine(keys.DirectoryPath, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Tenant.Value))), epochId.ToString("N") + ".epoch");

    private static async Task<byte[]> ReadAll(InMemoryContentStore store, ContentReference reference)
    {
        await using var body = (await store.ReadAsync(Tenant, reference, true, Reader)).Body;
        using var copy = new MemoryStream();
        await body.CopyToAsync(copy);
        return copy.ToArray();
    }
}

internal static class EpochTestExtensions
{
    // The node's bookkeeping truncates the WAL after a commit that reclaimed; a test that reads the database file asks for it.
    public static async ValueTask CheckpointAsyncOrSkip(this IContentBookkeeping bookkeeping) =>
        await ((NodeContentBookkeeping)bookkeeping).CheckpointAsync();
}
