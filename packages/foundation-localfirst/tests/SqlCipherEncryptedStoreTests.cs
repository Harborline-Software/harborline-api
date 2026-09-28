using Harborline.Api.Foundation.LocalFirst.Encryption;

namespace Harborline.Api.Foundation.LocalFirst.Tests;

public sealed class SqlCipherEncryptedStoreTests
{
    [Fact]
    public async Task ListKeysAsync_TreatsPercentInPrefixLiterally()
    {
        var directory = CreateTemporaryDirectory();
        var store = new SqlCipherEncryptedStore();
        try
        {
            await store.OpenAsync(
                Path.Combine(directory, "store.db"),
                TestKey(0x11),
                CancellationToken.None);
            await store.SetAsync("a%b/x", new byte[] { 1 }, CancellationToken.None);
            await store.SetAsync("aZb/x", new byte[] { 2 }, CancellationToken.None);

            var keys = await store.ListKeysAsync("a%b/", CancellationToken.None);

            Assert.Equal(new[] { "a%b/x" }, keys);
        }
        finally
        {
            await store.DisposeAsync();
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task ListKeysAsync_ReturnsOnlyKeysWithAnOrdinaryPrefix()
    {
        var directory = CreateTemporaryDirectory();
        var store = new SqlCipherEncryptedStore();
        try
        {
            await store.OpenAsync(
                Path.Combine(directory, "store.db"),
                TestKey(0x12),
                CancellationToken.None);
            await store.SetAsync("team/a", new byte[] { 1 }, CancellationToken.None);
            await store.SetAsync("team/b", new byte[] { 2 }, CancellationToken.None);
            await store.SetAsync("teammate/a", new byte[] { 3 }, CancellationToken.None);

            var keys = await store.ListKeysAsync("team/", CancellationToken.None);

            Assert.Equal(new[] { "team/a", "team/b" }, keys);
        }
        finally
        {
            await store.DisposeAsync();
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task CompareExchangeAsync_ReturnsFalseWhenTheExpectedValueDoesNotMatch()
    {
        var directory = CreateTemporaryDirectory();
        var store = new SqlCipherEncryptedStore();
        try
        {
            await store.OpenAsync(
                Path.Combine(directory, "store.db"),
                TestKey(0x13),
                CancellationToken.None);
            Assert.True(await store.CompareExchangeAsync(
                "membership", null, new byte[] { 1 }, CancellationToken.None));

            var exchanged = await store.CompareExchangeAsync(
                "membership", new byte[] { 2 }, new byte[] { 3 }, CancellationToken.None);

            Assert.False(exchanged);
            Assert.Equal(new byte[] { 1 }, await store.GetAsync("membership", CancellationToken.None));
        }
        finally
        {
            await store.DisposeAsync();
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task OpenAsync_UsesTheRequestedDatabasePathAcrossCloseAndReopen()
    {
        var directory = CreateTemporaryDirectory();
        var databasePath = Path.Combine(directory, "persisted.db");
        try
        {
            await using (var firstStore = new SqlCipherEncryptedStore())
            {
                await firstStore.OpenAsync(databasePath, TestKey(0x14), CancellationToken.None);
                Assert.True(await firstStore.CompareExchangeAsync(
                    "membership", null, new byte[] { 4 }, CancellationToken.None));
            }

            await using var reopenedStore = new SqlCipherEncryptedStore();
            await reopenedStore.OpenAsync(databasePath, TestKey(0x14), CancellationToken.None);

            Assert.Equal(new byte[] { 4 }, await reopenedStore.GetAsync("membership", CancellationToken.None));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task OpenAsync_RejectsAKeyThatCannotDecryptAnExistingDatabase()
    {
        var directory = CreateTemporaryDirectory();
        var databasePath = Path.Combine(directory, "encrypted.db");
        try
        {
            await using (var writer = new SqlCipherEncryptedStore())
            {
                await writer.OpenAsync(databasePath, TestKey(0x15), CancellationToken.None);
                Assert.True(await writer.CompareExchangeAsync(
                    "membership", null, new byte[] { 5 }, CancellationToken.None));
            }

            await using (var wrongKeyStore = new SqlCipherEncryptedStore())
            {
                await Assert.ThrowsAsync<InvalidKeyException>(() => wrongKeyStore.OpenAsync(
                    databasePath, TestKey(0x16), CancellationToken.None));
            }

            await using (var validKeyStore = new SqlCipherEncryptedStore())
            {
                await validKeyStore.OpenAsync(databasePath, TestKey(0x15), CancellationToken.None);
                Assert.Equal(new byte[] { 5 }, await validKeyStore.GetAsync("membership", CancellationToken.None));
            }
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task RotateKeyAsync_RejectsTheLegacyKeyAndPreservesDataForTheNewKey()
    {
        var directory = CreateTemporaryDirectory();
        var databasePath = Path.Combine(directory, "rotated.db");
        try
        {
            await using (var store = new SqlCipherEncryptedStore())
            {
                await store.OpenAsync(databasePath, TestKey(0x17), CancellationToken.None);
                Assert.True(await store.CompareExchangeAsync(
                    "membership", null, new byte[] { 7 }, CancellationToken.None));
                await store.RotateKeyAsync(TestKey(0x18), CancellationToken.None);
            }

            await using (var legacyKeyStore = new SqlCipherEncryptedStore())
            {
                await Assert.ThrowsAsync<InvalidKeyException>(() => legacyKeyStore.OpenAsync(
                    databasePath, TestKey(0x17), CancellationToken.None));
            }

            await using var rotatedKeyStore = new SqlCipherEncryptedStore();
            await rotatedKeyStore.OpenAsync(databasePath, TestKey(0x18), CancellationToken.None);

            Assert.Equal(new byte[] { 7 }, await rotatedKeyStore.GetAsync("membership", CancellationToken.None));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    private static byte[] TestKey(byte value) => Enumerable.Repeat(value, 32).ToArray();

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "harborline-sqlcipher-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteTemporaryDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
