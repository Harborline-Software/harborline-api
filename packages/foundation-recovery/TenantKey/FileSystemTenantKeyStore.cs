using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Harborline.Api.Foundation.Recovery.TenantKey;

/// <summary>Persists wrapped tenant-key hierarchy records in an install-owned directory.</summary>
public sealed class FileSystemTenantKeyStore : IStoredTenantKeyStore
{
    private readonly string _rootDirectory;
    private readonly Func<string, FileStream> _createStream;
    private readonly Action<string> _syncDirectory;

    /// <summary>Construct an opaque record store rooted at an install-owned directory.</summary>
    public FileSystemTenantKeyStore(string rootDirectory)
        : this(rootDirectory, path => new FileStream(
            path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true), SyncDirectory)
    {
    }

    internal FileSystemTenantKeyStore(
        string rootDirectory, Func<string, FileStream> createStream, Action<string> syncDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _rootDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        _createStream = createStream;
        _syncDirectory = syncDirectory;
    }

    /// <inheritdoc />
    public async Task<ReadOnlyMemory<byte>?> ReadAsync(string slot, CancellationToken ct)
    {
        var path = GetRecordPath(slot);
        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        SyncChain(Path.GetDirectoryName(path)!);
        return bytes;
    }

    /// <inheritdoc />
    public async Task<bool> TryCreateAsync(
        string slot,
        ReadOnlyMemory<byte> wrappedKey,
        CancellationToken ct)
    {
        var path = GetRecordPath(slot);
        var directory = Path.GetDirectoryName(path)!;
        var rootCreated = CreateRoot(_rootDirectory);
        Directory.CreateDirectory(directory);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = _createStream(temporary))
            {
                await stream.WriteAsync(wrappedKey, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                stream.Flush(flushToDisk: true);
            }

            var created = true;
            try
            {
                MoveDurably(temporary, path, overwrite: false);
            }
            catch (IOException) when (File.Exists(path))
            {
                created = false;
            }

            SyncChain(directory);
            if (rootCreated) _syncDirectory(Path.GetDirectoryName(_rootDirectory)!);
            return created;
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private void SyncChain(string directory)
    {
        for (var ancestor = new DirectoryInfo(directory); ancestor is not null; ancestor = ancestor.Parent)
        {
            _syncDirectory(ancestor.FullName);
            if (ancestor.FullName == _rootDirectory) break;
        }
    }

    private static bool CreateRoot(string root)
    {
        if (Directory.Exists(root)) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(root)!);
        var created = OperatingSystem.IsWindows() ? CreateDirectoryWindows(root, IntPtr.Zero) : MakeDirectory(root, 511) == 0;
        if (created) return true;
        var error = Marshal.GetLastWin32Error();
        if ((error == 17 || error == 183) && Directory.Exists(root)) return false;
        throw new Win32Exception(error);
    }

    private static void MoveDurably(string source, string destination, bool overwrite)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.Move(source, destination, overwrite);
            return;
        }
        // MOVEFILE_WRITE_THROUGH, plus MOVEFILE_REPLACE_EXISTING only for overwriting moves (ADR 0116 d5).
        if (!MoveFileEx(source, destination, overwrite ? 0x9u : 0x8u))
            throw new IOException("The key file could not be moved durably.", new Win32Exception(Marshal.GetLastWin32Error()));
    }

    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string source, string destination, uint flags);
    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryWindows(string path, IntPtr securityAttributes);
    [DllImport("libc", EntryPoint = "mkdir", SetLastError = true)]
    private static extern int MakeDirectory([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);

    private static void SyncDirectory(string directory)
    {
        if (OperatingSystem.IsWindows()) return;
        var descriptor = Open(directory, 0);
        if (descriptor < 0) throw new Win32Exception();
        try
        {
            if (Sync(descriptor) != 0) throw new Win32Exception();
            // Same policy as ADR 0114 d3: retain fsync on filesystems without F_FULLFSYNC support.
            if (OperatingSystem.IsMacOS()) _ = FullFsyncDescriptor(descriptor, 51);
        }
        finally
        {
            _ = Close(descriptor);
        }
    }

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int FullFsyncDescriptor(int descriptor, int command);
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int Sync(int descriptor);
    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int descriptor);

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

        if (!allowTrailingSeparator && slot.EndsWith(':', StringComparison.Ordinal))
        {
            throw new ArgumentException("A stored key record slot cannot end with a colon.", nameof(slot));
        }

        return slot.Split(':', StringSplitOptions.RemoveEmptyEntries);
    }
}
