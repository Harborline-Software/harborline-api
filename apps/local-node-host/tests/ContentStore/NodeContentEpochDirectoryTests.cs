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

        await NodeContentEpochKeyStore.WriteDurablyAsync(path, "wrapped-key"u8.ToArray(), default, directory =>
        {
            Assert.True(File.Exists(path));
            NodeContentByteStore.SyncDirectory(directory);
            synced.Add(directory);
        });

        Assert.Equal(new[] { tenant, epochs, storedKeys, _node.DataDirectory }, synced.Take(4));
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
            await NodeContentEpochKeyStore.WriteDurablyAsync(path, "wrapped-key"u8.ToArray(), default, directory =>
            {
                if (Path.GetFileName(directory) == failedDirectory) throw failure;
                NodeContentByteStore.SyncDirectory(directory);
            }));

        Assert.Same(failure, thrown);
    }
}
