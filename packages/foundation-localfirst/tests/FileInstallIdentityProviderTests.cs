using System.Runtime.InteropServices;
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

    // link(2): creates newPath only if it does not exist, atomically, and fails with EEXIST
    // otherwise. File.Copy(overwrite: false) is the atomic create-if-absent this box has.
    private static int SimulatedUnixLink(string oldPath, string newPath)
    {
        try
        {
            File.Copy(oldPath, newPath, overwrite: false);
            return 0;
        }
        catch (IOException) when (File.Exists(newPath))
        {
            Marshal.SetLastPInvokeError(17);
            return -1;
        }
    }

    [Fact]
    public async Task UnixPublish_WhenAnotherWriterPublishesInsideTheWindow_ObservesOneInstallIdentity()
    {
        var identityFilePath = Path.Combine(_directory, "unix-publish-race", "install.identity");
        var loser = new FileInstallIdentityProvider(identityFilePath) { UnixLink = SimulatedUnixLink };
        InstallIdentity? winnerIdentity = null;

        // The barrier is the window a non-atomic publish leaves open: after this writer decided
        // the path was free and before it publishes, a second writer publishes in full. An
        // existence check plus rename(2) would replace that record silently; link(2) loses.
        loser.PublishBarrier = async () =>
        {
            if (winnerIdentity is null)
            {
                var winner = new FileInstallIdentityProvider(identityFilePath) { UnixLink = SimulatedUnixLink };
                winnerIdentity = await winner.GetInstallIdentityAsync(CancellationToken.None);
            }
        };

        var observed = await loser.GetInstallIdentityAsync(CancellationToken.None);

        Assert.NotNull(winnerIdentity);
        Assert.Equal(winnerIdentity, observed);
        Assert.Equal(
            winnerIdentity!.Value.Value,
            await File.ReadAllTextAsync(identityFilePath, Encoding.ASCII));
    }

    [Fact]
    public async Task UnixPublish_ConcurrentFirstLaunchers_ObserveOneInstallIdentity()
    {
        var identityFilePath = Path.Combine(_directory, "unix-publish-launchers", "install.identity");
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var launches = Enumerable.Range(0, 16).Select(async _ =>
        {
            var provider = new FileInstallIdentityProvider(identityFilePath) { UnixLink = SimulatedUnixLink };
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
