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
        try
        {
            await using var stream = new FileStream(
                _identityFilePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            var bytes = Encoding.ASCII.GetBytes(identity.Value);
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
            return identity;
        }
        catch (IOException) when (File.Exists(_identityFilePath))
        {
            return await ReadWhenAvailableAsync(ct).ConfigureAwait(false);
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
