namespace Harborline.Api.Foundation.Recovery.TenantKey;

/// <summary>Persists wrapped tenant-key hierarchy records in an install-owned directory.</summary>
public sealed class FileSystemTenantKeyStore : IStoredTenantKeyStore
{
    private readonly string _rootDirectory;

    /// <summary>Construct an opaque record store rooted at an install-owned directory.</summary>
    public FileSystemTenantKeyStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _rootDirectory = Path.GetFullPath(rootDirectory);
    }

    /// <inheritdoc />
    public async Task<ReadOnlyMemory<byte>?> ReadAsync(string slot, CancellationToken ct)
    {
        var path = GetRecordPath(slot);
        try
        {
            return await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<bool> TryCreateAsync(
        string slot,
        ReadOnlyMemory<byte> wrappedKey,
        CancellationToken ct)
    {
        var path = GetRecordPath(slot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                useAsync: true);
            await stream.WriteAsync(wrappedKey, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (IOException) when (File.Exists(path))
        {
            return false;
        }
    }

    /// <inheritdoc />
    public Task DeleteAsync(string slot, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var path = GetRecordPath(slot);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteByPrefixAsync(string slotPrefix, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var directory = GetPrefixDirectory(slotPrefix);
        if (!Directory.Exists(directory))
        {
            return Task.CompletedTask;
        }

        foreach (var path in Directory.EnumerateFiles(directory, "*.key", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private string GetRecordPath(string slot)
    {
        var components = ValidateAndSplit(slot, allowTrailingSeparator: false);
        var directory = Path.Combine([_rootDirectory, .. components[..^1]]);
        return Path.Combine(directory, components[^1] + ".key");
    }

    private string GetPrefixDirectory(string slotPrefix)
    {
        var components = ValidateAndSplit(slotPrefix, allowTrailingSeparator: true);
        return Path.Combine([_rootDirectory, .. components]);
    }

    private static string[] ValidateAndSplit(string slot, bool allowTrailingSeparator)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slot);
        if (!slot.All(character =>
                char.IsAsciiLetterOrDigit(character) || character == ':'))
        {
            throw new ArgumentException("Stored key slots may contain only ASCII letters, digits, and colons.", nameof(slot));
        }

        if (!allowTrailingSeparator && slot.EndsWith(':'))
        {
            throw new ArgumentException("A stored key record slot cannot end with a colon.", nameof(slot));
        }

        return slot.Split(':', StringSplitOptions.RemoveEmptyEntries);
    }
}
