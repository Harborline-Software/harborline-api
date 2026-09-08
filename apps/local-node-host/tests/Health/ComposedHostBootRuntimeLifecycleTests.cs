using System.Net;

using Harborline.Api.LocalNodeHost;

namespace Harborline.Api.LocalNodeHost.Tests.Health;

[Collection("Harborline process environment")]
public sealed class ComposedHostBootRuntimeLifecycleTests
{
    private const string RootSeedHex =
        "3463463463463463463463463463463463463463463463463463463463463463";

    [Fact]
    public async Task Faulted_composition_surfaces_its_error_and_releases_the_runtime_for_a_healthy_boot()
    {
        var faultedDirectory = CreateDataDirectory();
        var healthyDirectory = CreateDataDirectory();
        var previousRootSeedHex = Environment.GetEnvironmentVariable("LocalNode__RootSeedHex");
        var healthyStarted = false;
        try
        {
            Environment.SetEnvironmentVariable("LocalNode__RootSeedHex", RootSeedHex);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                LocalNodeHostRuntime.StartAsync(
                    "ticket-346-faulted-composition",
                    faultedDirectory,
                    CancellationToken.None,
                    finalServiceRegistration: _ =>
                        throw new InvalidOperationException("ticket-346 composition fault")));
            Assert.Equal("ticket-346 composition fault", exception.Message);

            var address = await LocalNodeHostRuntime.StartAsync(
                "ticket-346-healthy-composition", healthyDirectory, CancellationToken.None);
            healthyStarted = true;
            using var client = new HttpClient { BaseAddress = address };
            using var health = await client.GetAsync("/health");
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);

            await LocalNodeHostRuntime.StopAsync(CancellationToken.None);
            healthyStarted = false;
        }
        finally
        {
            if (healthyStarted)
                await LocalNodeHostRuntime.StopAsync(CancellationToken.None);

            Environment.SetEnvironmentVariable("LocalNode__RootSeedHex", previousRootSeedHex);
            DeleteDataDirectory(faultedDirectory);
            DeleteDataDirectory(healthyDirectory);
        }
    }

    [Fact]
    public async Task Concurrent_boot_is_refused_while_a_healthy_host_is_running()
    {
        var firstDirectory = CreateDataDirectory();
        var secondDirectory = CreateDataDirectory();
        var previousRootSeedHex = Environment.GetEnvironmentVariable("LocalNode__RootSeedHex");
        var healthyStarted = false;
        try
        {
            Environment.SetEnvironmentVariable("LocalNode__RootSeedHex", RootSeedHex);
            var address = await LocalNodeHostRuntime.StartAsync(
                "ticket-346-first-healthy-composition", firstDirectory, CancellationToken.None);
            healthyStarted = true;
            using var client = new HttpClient { BaseAddress = address };
            using var health = await client.GetAsync("/health");
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);

            var exception = Assert.Throws<InvalidOperationException>(() =>
                LocalNodeHostRuntime.StartAsync(
                    "ticket-346-second-healthy-composition", secondDirectory, CancellationToken.None));
            Assert.Equal("The local-node host is already running.", exception.Message);
        }
        finally
        {
            if (healthyStarted)
                await LocalNodeHostRuntime.StopAsync(CancellationToken.None);

            Environment.SetEnvironmentVariable("LocalNode__RootSeedHex", previousRootSeedHex);
            DeleteDataDirectory(firstDirectory);
            DeleteDataDirectory(secondDirectory);
        }
    }

    private static string CreateDataDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ticket-346-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteDataDirectory(string directory)
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}
