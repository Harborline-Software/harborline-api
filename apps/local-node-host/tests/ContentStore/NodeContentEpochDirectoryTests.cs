using Harborline.Api.LocalNodeHost.Data.ContentStore;
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

        await NodeContentEpochKeyStore.WriteDurablyAsync(path, "wrapped-key"u8.ToArray(), default, epochs, directory =>
        {
            Assert.True(File.Exists(path));
            NodeContentByteStore.SyncDirectory(directory);
            synced.Add(directory);
        });

        Assert.Equal(alreadyCreated ? new[] { tenant, epochs } : new[] { tenant, epochs, storedKeys }, synced);
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
        await NodeContentEpochKeyStore.WriteDurablyAsync(path, "wrapped-key"u8.ToArray(), default, root, directory =>
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
            await NodeContentEpochKeyStore.WriteDurablyAsync(path, "wrapped-key"u8.ToArray(), default, Path.Combine(_node.DataDirectory, "tenant-keys", "content-epochs"), directory =>
            {
                if (Path.GetFileName(directory) == failedDirectory) throw failure;
                NodeContentByteStore.SyncDirectory(directory);
            }));

        Assert.Same(failure, thrown);
    }
}
