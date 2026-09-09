using System.Net;

using Harborline.Api.LocalNodeHost;

namespace Harborline.Api.LocalNodeHost.Tests.Health;

[Collection("Harborline process environment")]
public sealed class ComposedHostBootRuntimeLifecycleTests
{
    private const string RootSeedHex =
        "3463463463463463463463463463463463463463463463463463463463463463";

    [Fact]
    public async Task Canceled_start_releases_the_runtime_for_a_healthy_boot()
    {
        var canceledDirectory = CreateDataDirectory();
        var faultedDirectory = CreateDataDirectory();
        var healthyDirectory = CreateDataDirectory();
        var previousRootSeedHex = Environment.GetEnvironmentVariable("LocalNode__RootSeedHex");
        var compositionEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var compositionMayContinue = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var canceled = new CancellationTokenSource();
        try
        {
            Environment.SetEnvironmentVariable("LocalNode__RootSeedHex", RootSeedHex);

            var canceledStart = Task.Run(() => LocalNodeHostRuntime.StartAsync(
                "ticket-346-canceled-composition",
                canceledDirectory,
                canceled.Token,
                finalServiceRegistration: _ =>
                {
                    compositionEntered.TrySetResult();
                    compositionMayContinue.Task.GetAwaiter().GetResult();
                }));
            await compositionEntered.Task;
            canceled.Cancel();
            compositionMayContinue.TrySetResult();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledStart);

            var fault = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                LocalNodeHostRuntime.StartAsync(
                    "ticket-346-faulted-composition",
                    faultedDirectory,
                    CancellationToken.None,
                    finalServiceRegistration: _ =>
                        throw new InvalidOperationException("ticket-346 composition fault")));
            Assert.Equal("ticket-346 composition fault", fault.Message);

            var address = await LocalNodeHostRuntime.StartAsync(
                "ticket-346-healthy-composition", healthyDirectory, CancellationToken.None);
            using var client = new HttpClient { BaseAddress = address };
            using var health = await client.GetAsync("/health");
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);

            await LocalNodeHostRuntime.StopAsync(CancellationToken.None);
        }
        finally
        {
            compositionMayContinue.TrySetResult();
            await LocalNodeHostRuntime.StopAsync(CancellationToken.None);
            Environment.SetEnvironmentVariable("LocalNode__RootSeedHex", previousRootSeedHex);
            DeleteDataDirectory(canceledDirectory);
            DeleteDataDirectory(faultedDirectory);
            DeleteDataDirectory(healthyDirectory);
        }
    }

    [Fact]
    public async Task Canceled_start_does_not_weaken_the_concurrent_boot_guard()
    {
        var canceledDirectory = CreateDataDirectory();
        var firstDirectory = CreateDataDirectory();
        var secondDirectory = CreateDataDirectory();
        var previousRootSeedHex = Environment.GetEnvironmentVariable("LocalNode__RootSeedHex");
        var compositionEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var compositionMayContinue = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var canceled = new CancellationTokenSource();
        try
        {
            Environment.SetEnvironmentVariable("LocalNode__RootSeedHex", RootSeedHex);

            var canceledStart = Task.Run(() => LocalNodeHostRuntime.StartAsync(
                "ticket-346-canceled-before-concurrent-guard",
                canceledDirectory,
                canceled.Token,
                finalServiceRegistration: _ =>
                {
                    compositionEntered.TrySetResult();
                    compositionMayContinue.Task.GetAwaiter().GetResult();
                }));
            await compositionEntered.Task;
            canceled.Cancel();
            compositionMayContinue.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledStart);

            var address = await LocalNodeHostRuntime.StartAsync(
                "ticket-346-first-healthy-composition", firstDirectory, CancellationToken.None);
            using var client = new HttpClient { BaseAddress = address };
            using var health = await client.GetAsync("/health");
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                LocalNodeHostRuntime.StartAsync(
                    "ticket-346-second-healthy-composition", secondDirectory, CancellationToken.None));
            Assert.Equal("The local-node host is already running.", exception.Message);
        }
        finally
        {
            compositionMayContinue.TrySetResult();
            await LocalNodeHostRuntime.StopAsync(CancellationToken.None);
            Environment.SetEnvironmentVariable("LocalNode__RootSeedHex", previousRootSeedHex);
            DeleteDataDirectory(canceledDirectory);
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
