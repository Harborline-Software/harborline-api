using Harborline.Api.Foundation.LocalFirst.Encryption;
using System.Runtime.Versioning;

namespace Harborline.Api.Foundation.LocalFirst.Tests;

public sealed class WindowsDpapiKeystoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "harborline-dpapi-" + Guid.NewGuid().ToString("N"));

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task ConfiguredStorageDirectory_IsUsedForDpapiKeyFiles()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var keystore = new WindowsDpapiKeystore(_directory);

        await keystore.SetKeyAsync("team-key", new byte[] { 1, 2, 3 }, CancellationToken.None);

        Assert.Equal(_directory, keystore.StorageDirectory);
        Assert.True(File.Exists(Path.Combine(_directory, "team-key.dpapi")));
        Assert.Equal(
            new byte[] { 1, 2, 3 },
            (await keystore.GetKeyAsync("team-key", CancellationToken.None))?.ToArray());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
