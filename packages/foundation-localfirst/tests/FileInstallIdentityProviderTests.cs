using System.Text;

using Harborline.Api.Foundation.LocalFirst.Installation;

namespace Harborline.Api.Foundation.LocalFirst.Tests;

public sealed class FileInstallIdentityProviderTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "harborline-install-identity-publish-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ConcurrentLauncher_NeverObservesAPartialRecordAtTheIdentityPath()
    {
        var identityFilePath = Path.Combine(_directory, "publish-window", "install.identity");
        var provider = new FileInstallIdentityProvider(identityFilePath);
        var observedExists = false;
        string? observedContent = null;

        // The barrier runs in the window between a durable record and a visible one. A launcher
        // on a platform that does not enforce FileShare (macOS, where the gate failed) reads
        // whatever bytes sit at the identity path in exactly this window.
        provider.PublishBarrier = () =>
        {
            observedExists = File.Exists(identityFilePath);
            if (observedExists)
            {
                using var stream = new FileStream(
                    identityFilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite);
                using var reader = new StreamReader(stream, Encoding.ASCII);
                observedContent = reader.ReadToEnd();
            }

            return Task.CompletedTask;
        };

        var published = await provider.GetInstallIdentityAsync(CancellationToken.None);

        Assert.False(
            observedExists,
            $"the identity path was visible holding '{observedContent}' before the record was published");
        var laterLauncher = new FileInstallIdentityProvider(identityFilePath);
        Assert.Equal(published, await laterLauncher.GetInstallIdentityAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ConcurrentFirstLaunchers_AcrossThePublishWindow_ObserveOneInstallIdentity()
    {
        var identityFilePath = Path.Combine(_directory, "publish-race", "install.identity");
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var launches = Enumerable.Range(0, 16).Select(async _ =>
        {
            var provider = new FileInstallIdentityProvider(identityFilePath);
            await start.Task;
            return await provider.GetInstallIdentityAsync(CancellationToken.None);
        }).ToArray();

        start.SetResult();
        var identities = await Task.WhenAll(launches);

        Assert.Single(identities.Distinct());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
