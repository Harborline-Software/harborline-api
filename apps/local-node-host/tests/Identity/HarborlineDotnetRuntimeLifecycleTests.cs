using System.Security.Cryptography;

using Harborline.Api.LocalNodeHost;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

/// <summary>B2: composition faults complete the caller task and do not poison later starts.</summary>
[Collection("Harborline process environment")]
public sealed class HarborlineRuntimeLifecycleTests
{
    [Fact(DisplayName = "B2: a composition failure surfaces quickly and the runtime can restart")]
    public async Task CompositionFailure_IsObserved_AndStaticsReset()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), $"harborline-node-b2-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDirectory);
        var previousRootSeedHex = Environment.GetEnvironmentVariable("LocalNode__RootSeedHex");
        try
        {
            Environment.SetEnvironmentVariable("LocalNode__RootSeedHex", "not-hex");
            var failedStart = LocalNodeHostRuntime.StartAsync(
                "harborline-b2-token", dataDirectory, CancellationToken.None);
            await Assert.ThrowsAnyAsync<Exception>(() => failedStart.WaitAsync(TimeSpan.FromSeconds(5)));

            Environment.SetEnvironmentVariable(
                "LocalNode__RootSeedHex",
                Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
            var address = await LocalNodeHostRuntime.StartAsync(
                "harborline-b2-token", dataDirectory, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(address.IsLoopback);
            await LocalNodeHostRuntime.StopAsync(CancellationToken.None);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LocalNode__RootSeedHex", previousRootSeedHex);
            if (Directory.Exists(dataDirectory))
                Directory.Delete(dataDirectory, recursive: true);
        }
    }
}
