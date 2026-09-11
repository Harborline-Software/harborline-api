using System.Runtime.InteropServices;
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
public sealed partial class FileInstallIdentityProvider : IInstallIdentityProvider
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

    // Test seam: the POSIX link(2) the Unix publish path uses. Injecting it exercises that path,
    // and its races, on Windows. Production leaves it null.
    internal Func<string, string, int>? UnixLink { get; set; }

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

            // The publish is also the election, and it must be atomic on every platform: either
            // the whole record becomes visible at the identity path, or this launcher lost and
            // reads the winner's complete record. A concurrent launcher never sees a partial
            // record, which an in-place write exposed between create and write.
            if (!TryPublish(temporaryPath))
            {
                return await ReadWhenAvailableAsync(ct).ConfigureAwait(false);
            }

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

    /// <summary>Publishes the candidate at the identity path, or reports that another writer won.</summary>
    private bool TryPublish(string temporaryPath)
    {
        if (UnixLink is null && OperatingSystem.IsWindows())
        {
            try
            {
                File.Move(temporaryPath, _identityFilePath, overwrite: false);
                return true;
            }
            catch (IOException) when (File.Exists(_identityFilePath))
            {
                return false;
            }
        }

        var link = UnixLink ?? Link;
        if (link(temporaryPath, _identityFilePath) == 0)
        {
            return true;
        }

        var errno = Marshal.GetLastPInvokeError();
        if (errno == Eexist)
        {
            return false;
        }

        throw new IOException(
            $"Publishing install identity record '{_identityFilePath}' failed with errno {errno}.");
    }

    // EEXIST is 17 on Linux and on macOS.
    private const int Eexist = 17;

    [LibraryImport("libc", EntryPoint = "link", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Link(string oldPath, string newPath);

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
