using System.Text;

namespace Harborline.Api.Foundation.LocalFirst.Installation;

/// <summary>
/// Persists a random install identity in an installer-owned stable file.
/// </summary>
/// <remarks>
/// The file path locates the install across restarts and in-place upgrades; it does not determine
/// the identity value. The first opener atomically creates a random value, so two installs cannot
/// collide because their tenant or environment labels happen to match.
/// </remarks>
public sealed class FileInstallIdentityProvider : IInstallIdentityProvider
{
    private readonly string _identityFilePath;

    /// <summary>Creates a provider for one stable identity record.</summary>
    /// <param name="identityFilePath">Installer-owned path retained across upgrades.</param>
    public FileInstallIdentityProvider(string identityFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identityFilePath);
        _identityFilePath = Path.GetFullPath(identityFilePath);
    }

    /// <summary>Path containing the durable identity record.</summary>
    public string IdentityFilePath => _identityFilePath;

    // Test seam: runs after a new record is durable and before it becomes visible at the
    // identity path, so a test can observe what a concurrent launcher would see in that window.
    internal Func<Task>? PublishBarrier { get; set; }

    /// <inheritdoc />
    public async ValueTask<InstallIdentity> GetInstallIdentityAsync(CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(_identityFilePath)
            ?? throw new InvalidOperationException("Install identity path must have a parent directory.");
        Directory.CreateDirectory(directory);

        if (File.Exists(_identityFilePath))
        {
            return await ReadWhenAvailableAsync(ct).ConfigureAwait(false);
        }

        var identity = InstallIdentity.New();
        var temporaryPath = $"{_identityFilePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                var bytes = Encoding.ASCII.GetBytes(identity.Value);
                await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }

            if (PublishBarrier is { } barrier)
            {
                await barrier().ConfigureAwait(false);
            }

            // The record becomes visible at the identity path only as a whole: a concurrent
            // launcher either does not see the path or reads a complete record. An in-place
            // write exposed an empty file between create and write, which readers refused.
            File.Move(temporaryPath, _identityFilePath, overwrite: false);
            return identity;
        }
        catch (IOException) when (File.Exists(_identityFilePath))
        {
            return await ReadWhenAvailableAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private async Task<InstallIdentity> ReadWhenAvailableAsync(CancellationToken ct)
    {
        while (true)
        {
            try
            {
                var text = await File.ReadAllTextAsync(_identityFilePath, Encoding.ASCII, ct)
                    .ConfigureAwait(false);
                try
                {
                    return InstallIdentity.Parse(text);
                }
                catch (FormatException ex)
                {
                    throw new InvalidDataException(
                        $"Install identity record '{_identityFilePath}' is invalid; refusing to replace the install identity.",
                        ex);
                }
            }
            catch (IOException) when (File.Exists(_identityFilePath))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), ct).ConfigureAwait(false);
            }
        }
    }
}
