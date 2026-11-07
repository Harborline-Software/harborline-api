using System.Buffers.Binary;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Harborline.Foundation.ContentStore;
using Microsoft.Win32.SafeHandles;

namespace Harborline.Api.LocalNodeHost.Data.ContentStore;

/// <summary>The byte port: one write-once file per item beside the node's database (ADR 0114 decisions 1 to 3).</summary>
/// <remarks>
/// A file is named by the item's opaque key in lower-case hex, in a subdirectory named by the key's first two characters, so no
/// content id or display name reaches a path (CWE-22, CWE-200). It holds a header with nothing secret and then each segment's
/// lengths, ciphertext and tag. Every segment but the last has the first one's size, so a read seeks straight to its index. A
/// file's write time is the server time the port was told, which the sweep's grace period counts from. A single lock makes
/// completing a write and deleting by age one atomic step, so this store must be the only one over its directory.
/// </remarks>
public sealed partial class NodeContentByteStore : IContentByteStore
{
    private const string Temporary = ".tmp";
    private const int FrameHeaderBytes = 8;
    private const int FullFsyncCommand = 51; // F_FULLFSYNC, <sys/fcntl.h>
    private static readonly byte[] Magic = "HLCSBYT1"u8.ToArray();
    private readonly object _gate = new();

    /// <summary>Creates the byte port over a directory the node owns.</summary>
    public NodeContentByteStore(string directory)
    {
        DirectoryPath = Path.GetFullPath(directory);
        var created = !Directory.Exists(DirectoryPath);
        Directory.CreateDirectory(DirectoryPath);
        if (created) SyncDirectory(Path.GetDirectoryName(DirectoryPath)!);
    }

    /// <summary>The content directory.</summary>
    public string DirectoryPath { get; }

    /// <inheritdoc />
    public async ValueTask<IContentByteWriter> OpenWriteAsync(Guid itemKey, DateTimeOffset startedUtc, CancellationToken cancellationToken = default)
    {
        var path = PathOf(itemKey);
        if (File.Exists(path)) throw new IOException("An item's bytes are write-once.");
        var directory = Path.GetDirectoryName(path)!;
        var created = !Directory.Exists(directory);
        Directory.CreateDirectory(directory);
        if (created) SyncDirectory(DirectoryPath);
        var stream = new FileStream(path + Temporary, FileMode.CreateNew, FileAccess.Write, FileShare.Read | FileShare.Delete, 0, FileOptions.Asynchronous);
        try
        {
            await stream.WriteAsync(Magic, cancellationToken).ConfigureAwait(false);
            File.SetLastWriteTimeUtc(stream.SafeFileHandle, startedUtc.UtcDateTime);
            return new Writer(this, path, stream, startedUtc);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            DeleteQuietly(path + Temporary);
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask<ContentCiphertextSegment?> ReadSegmentAsync(Guid itemKey, int index, CancellationToken cancellationToken = default)
    {
        if (index < 0) return null;
        FileStream stream;
        try
        {
            // FileShare.Delete, so a reader never stops a removal, which Windows would otherwise refuse (ADR 0114 decision 3).
            stream = new FileStream(PathOf(itemKey), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 0, FileOptions.Asynchronous);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }

        await using (stream.ConfigureAwait(false))
        {
            // A file that fails any of these checks has no such segment; the store treats that as an integrity failure.
            var header = new byte[Magic.Length + FrameHeaderBytes];
            if (!await ReadAllAsync(stream, header, cancellationToken).ConfigureAwait(false) || !header.AsSpan(0, Magic.Length).SequenceEqual(Magic)) return null;
            var (firstCiphertext, firstTag) = Lengths(header.AsSpan(Magic.Length));
            if (firstCiphertext < 0 || firstTag < 0 || stream.Length - Magic.Length - FrameHeaderBytes < (long)firstCiphertext + firstTag) return null;
            var frame = (long)FrameHeaderBytes + firstCiphertext + firstTag;
            stream.Seek(Magic.Length + index * frame, SeekOrigin.Begin);
            var lengths = new byte[FrameHeaderBytes];
            if (!await ReadAllAsync(stream, lengths, cancellationToken).ConfigureAwait(false)) return null;
            var (ciphertextLength, tagLength) = Lengths(lengths);
            // Checked against the file, so a corrupt length never sizes a buffer.
            if (ciphertextLength < 0 || ciphertextLength > firstCiphertext || tagLength != firstTag || stream.Length - stream.Position < (long)ciphertextLength + tagLength) return null;
            var ciphertext = new byte[ciphertextLength];
            var tag = new byte[tagLength];
            if (!await ReadAllAsync(stream, ciphertext, cancellationToken).ConfigureAwait(false) || !await ReadAllAsync(stream, tag, cancellationToken).ConfigureAwait(false)) return null;
            return new(ciphertext, tag);
        }
    }

    /// <inheritdoc />
    public ValueTask DeleteAsync(Guid itemKey, CancellationToken cancellationToken = default)
    {
        lock (_gate) Delete(itemKey);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<bool> DeleteIfWrittenAtOrBeforeAsync(Guid itemKey, DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var path = PathOf(itemKey);
            var current = File.Exists(path) ? path : path + Temporary;
            if (!File.Exists(current) || File.GetLastWriteTimeUtc(current) > cutoff.UtcDateTime) return ValueTask.FromResult(false);
            Delete(itemKey);
            return ValueTask.FromResult(true);
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<Guid>> WrittenAtOrBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        var keys = new HashSet<Guid>();
        foreach (var path in Directory.EnumerateFiles(DirectoryPath, "*", SearchOption.AllDirectories))
        {
            // A name this store did not write is left alone: nothing here deletes what it cannot name.
            var name = Path.GetFileName(path);
            if (name.EndsWith(Temporary, StringComparison.Ordinal)) name = name[..^Temporary.Length];
            if (!Guid.TryParseExact(name, "N", out var key) || !string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), name[..2], StringComparison.Ordinal)) continue;
            if (File.GetLastWriteTimeUtc(path) <= cutoff.UtcDateTime) keys.Add(key);
        }

        return ValueTask.FromResult<IReadOnlyList<Guid>>([.. keys]);
    }

    /// <summary>
    /// Flushes a file to the device. .NET's <see cref="FileStream.Flush(bool)"/> asks for <c>F_FULLFSYNC</c> on macOS (dotnet/runtime
    /// <c>pal_io.c</c>, <c>SystemNative_FSync</c>), and plain <c>fsync</c> there would stop at the drive's cache, so macOS also asks
    /// directly rather than rest durability on a runtime detail. Answers whether that direct request succeeded.
    /// </summary>
    internal static bool FlushToDevice(FileStream stream)
    {
        stream.Flush(flushToDisk: true);
        return OperatingSystem.IsMacOS() && FullFsync(stream.SafeFileHandle, FullFsyncCommand) != -1;
    }

    private string PathOf(Guid itemKey)
    {
        var name = itemKey.ToString("N", CultureInfo.InvariantCulture);
        return Path.Combine(DirectoryPath, name[..2], name);
    }

    private void Delete(Guid itemKey)
    {
        var path = PathOf(itemKey);
        File.Delete(path);
        File.Delete(path + Temporary);
    }

    private static void DeleteQuietly(string path)
    {
        try { File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Left for the sweep, which reclaims bytes no entry names.
        }
    }

    private static (int Ciphertext, int Tag) Lengths(ReadOnlySpan<byte> frame) =>
        (BinaryPrimitives.ReadInt32LittleEndian(frame), BinaryPrimitives.ReadInt32LittleEndian(frame[4..]));

    private static async ValueTask<bool> ReadAllAsync(FileStream stream, byte[] buffer, CancellationToken cancellationToken) =>
        await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false) == buffer.Length;

    // fsync on a file does not cover its directory entry (fsync(2)), so a rename is durable only after this. Windows has no equivalent.
    private static void SyncDirectory(string directory)
    {
        if (OperatingSystem.IsWindows()) return;
        var descriptor = Open(directory, 0);
        if (descriptor < 0) throw new Win32Exception();
        try
        {
            if (Sync(descriptor) != 0) throw new Win32Exception();
        }
        finally
        {
            _ = Close(descriptor);
        }
    }

    [LibraryImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static partial int FullFsync(SafeFileHandle file, int command);
    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Open(string path, int flags);
    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static partial int Sync(int descriptor);
    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int Close(int descriptor);

    private sealed class Writer(NodeContentByteStore owner, string path, FileStream stream, DateTimeOffset startedUtc) : IContentByteWriter
    {
        private int? _segmentBytes;
        private bool _shortSeen;
        private bool _completed;
        private bool _closed;

        public async ValueTask AppendAsync(ContentCiphertextSegment segment, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(segment);
            // Only the last segment may be short, because a read computes every offset from the first.
            if (_shortSeen || segment.Ciphertext.Length > (_segmentBytes ?? int.MaxValue)) throw new InvalidOperationException("Only an item's last segment may be shorter than its first.");
            _shortSeen = segment.Ciphertext.Length < (_segmentBytes ??= segment.Ciphertext.Length);
            var lengths = new byte[FrameHeaderBytes];
            BinaryPrimitives.WriteInt32LittleEndian(lengths, segment.Ciphertext.Length);
            BinaryPrimitives.WriteInt32LittleEndian(lengths.AsSpan(4), segment.Tag.Length);
            await stream.WriteAsync(lengths, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(segment.Ciphertext, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(segment.Tag, cancellationToken).ConfigureAwait(false);
            File.SetLastWriteTimeUtc(stream.SafeFileHandle, startedUtc.UtcDateTime);
        }

        public async ValueTask CompleteAsync(DateTimeOffset durableUtc, CancellationToken cancellationToken = default)
        {
            File.SetLastWriteTimeUtc(stream.SafeFileHandle, durableUtc.UtcDateTime);
            FlushToDevice(stream);
            await stream.DisposeAsync().ConfigureAwait(false);
            _closed = true;
            lock (owner._gate)
            {
                // A sweep that outran a very slow upload took its bytes, and the move then throws: the put must fail rather than stage nothing.
                File.Move(path + Temporary, path);
                _completed = true;
            }

            SyncDirectory(Path.GetDirectoryName(path)!);
        }

        public async ValueTask DisposeAsync()
        {
            if (!_closed) await stream.DisposeAsync().ConfigureAwait(false);
            if (!_completed) DeleteQuietly(path + Temporary);
        }
    }
}
