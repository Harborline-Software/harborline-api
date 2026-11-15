using Harborline.Api.LocalNodeHost.Data.ContentStore;
using Harborline.Foundation.ContentStore;
using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.ContentStore;

public sealed partial class NodeContentStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Epoch_clause_3_the_key_write_syncs_every_directory_entry_even_after_an_interrupted_creation(bool alreadyCreated)
    {
        var storedKeys = Path.Combine(_node.DataDirectory, "tenant-keys");
        var epochs = Path.Combine(storedKeys, "content-epochs");
        var tenant = Path.Combine(epochs, "test-tenant");
        var path = Path.Combine(tenant, "test.epoch");
        if (alreadyCreated) Directory.CreateDirectory(tenant);
        var synced = new List<string>();

        await NodeContentEpochKeyStore.WriteDurablyAsync(path, "wrapped-key"u8.ToArray(), epochs, directory =>
        {
            Assert.True(File.Exists(path));
            NodeContentByteStore.SyncDirectory(directory);
            synced.Add(directory);
        });

        // tenant-keys and content-epochs are both new when the data directory is all that exists, so each new entry's parent is
        // synced, climbing, and nothing above the data directory (2026-11-12 ruling (1)).
        Assert.Equal(alreadyCreated ? new[] { tenant, epochs } : new[] { tenant, epochs, storedKeys, _node.DataDirectory }, synced);
        Assert.Equal("wrapped-key", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Epoch_write_with_existing_root_succeeds_when_sync_above_root_is_forbidden()
    {
        var root = Path.Combine(_node.DataDirectory, "tenant-keys", "content-epochs");
        var tenant = Path.Combine(root, "test-tenant");
        Directory.CreateDirectory(root);
        var path = Path.Combine(tenant, "test.epoch");
        var synced = new List<string>();
        await NodeContentEpochKeyStore.WriteDurablyAsync(path, "wrapped-key"u8.ToArray(), root, directory =>
        {
            if (directory != root && directory != tenant) throw new IOException("above root");
            synced.Add(directory);
        });
        Assert.Equal(new[] { tenant, root }, synced);
        Assert.Equal("wrapped-key", await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData("content-epochs")]
    [InlineData("tenant-keys")]
    public async Task Epoch_clause_3_a_parent_sync_failure_must_refuse_the_write(string failedDirectory)
    {
        var path = Path.Combine(_node.DataDirectory, "tenant-keys", "content-epochs", "test-tenant", "test.epoch");
        var failure = new IOException("injected parent-directory sync failure");

        var thrown = await Assert.ThrowsAsync<IOException>(async () =>
            await NodeContentEpochKeyStore.WriteDurablyAsync(path, "wrapped-key"u8.ToArray(), Path.Combine(_node.DataDirectory, "tenant-keys", "content-epochs"), directory =>
            {
                if (Path.GetFileName(directory) == failedDirectory) throw failure;
                NodeContentByteStore.SyncDirectory(directory);
            }));

        Assert.Same(failure, thrown);
    }

    // Oracle: the 2026-11-12 M11 ruling (1). A barrier owed for a failed sync stays owed until it succeeds, and every
    // instance completes it before a read or write returns.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Epoch_an_owed_parent_sync_is_completed_before_a_retried_write_returns(bool sameInstance)
    {
        var storedKeys = Path.Combine(_node.DataDirectory, "tenant-keys");
        var epochs = Path.Combine(storedKeys, "content-epochs");
        Directory.CreateDirectory(storedKeys);
        var failParent = true;
        var synced = new List<string>();
        Action<string> sync = directory =>
        {
            if (failParent && directory == storedKeys) throw new IOException("parent sync failure");
            synced.Add(directory);
        };
        using var first = new NodeContentEpochKeyStore(epochs, sync);
        await Assert.ThrowsAsync<IOException>(async () => await first.CreateAsync(Tenant, NewEpochKey()));
        failParent = false;
        synced.Clear();

        using var other = new NodeContentEpochKeyStore(epochs, sync);
        var retry = sameInstance ? first : other;
        await retry.CreateAsync(Tenant, NewEpochKey());

        Assert.Contains(storedKeys, synced);
        synced.Clear();
        await retry.CreateAsync(Tenant, NewEpochKey());
        Assert.DoesNotContain(storedKeys, synced);
    }

    [Fact]
    public async Task Epoch_a_read_after_a_failed_write_completes_the_owed_parent_sync_or_refuses()
    {
        var storedKeys = Path.Combine(_node.DataDirectory, "tenant-keys");
        var epochs = Path.Combine(storedKeys, "content-epochs");
        Directory.CreateDirectory(storedKeys);
        var failure = new IOException("parent sync failure");
        var epoch = NewEpochKey();
        using var failing = new NodeContentEpochKeyStore(epochs, directory =>
        {
            if (directory == storedKeys) throw failure;
        });
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(async () => await failing.CreateAsync(Tenant, epoch)));
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(async () => await failing.GetAsync(Tenant, epoch.EpochId)));

        var synced = new List<string>();
        using var reader = new NodeContentEpochKeyStore(epochs, synced.Add);
        Assert.NotNull(await reader.GetAsync(Tenant, epoch.EpochId));
        Assert.Equal(new[] { storedKeys }, synced);
    }

    private static ContentEpochKey NewEpochKey() => new(Guid.NewGuid(), new byte[32], new byte[12], new byte[16]);
}
