using System.Security.Cryptography;
using System.Text;
using Harborline.Foundation.Assets.Common;
using Harborline.Foundation.ContentStore;

namespace Harborline.Api.LocalNodeHost.Data.ContentStore;

/// <summary>
/// A tenant's content-store epoch keys as files in the node's stored-key store, beside the version-2 tenant keys and never in
/// <c>local-node.db</c>, a backup or sync (ADR 0116 decision 4). Each epoch is one file holding only the key as the store
/// wrapped it with the tenant's data-key-wrap subkey. A tenant's <c>current</c> file names its current epoch.
/// </summary>
/// <remarks>
/// Every file is written to a temporary name, flushed to the device, renamed into place and its directory flushed, so a key
/// is durable before any row can name it (ADR 0116 decision 5) and a crash leaves either the old file or the new one.
/// </remarks>
public sealed class NodeContentEpochKeyStore : IContentEpochKeyStore, IDisposable
{
    /// <summary>The store's directory under the node's <c>tenant-keys</c>.</summary>
    public const string DirectoryName = "content-epochs";

    private const string CurrentName = "current";
    private const string Extension = ".epoch";
    private static readonly byte[] Magic = "HLCSEPK1"u8.ToArray();
    private readonly string _root;
    internal static Action<string>? AfterDurableStep { get; set; }
    // ponytail: one node process writes this directory, so one gate orders the compare-and-switch; several writers need a file lock.
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Opens the store at <paramref name="directory"/>, which is created on first write.</summary>
    public NodeContentEpochKeyStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _root = Path.GetFullPath(directory);
    }

    /// <summary>The store's directory.</summary>
    public string DirectoryPath => _root;

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    /// <inheritdoc />
    public async ValueTask<ContentEpochKey?> GetCurrentAsync(TenantId tenant, CancellationToken cancellationToken = default)
    {
        var current = await ReadCurrentAsync(tenant, cancellationToken).ConfigureAwait(false);
        return current is null ? null : await GetAsync(tenant, current.Value, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<ContentEpochKey?> GetAsync(TenantId tenant, Guid epochId, CancellationToken cancellationToken = default)
    {
        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(EpochPath(tenant, epochId), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }

        return Decode(epochId, bytes);
    }

    /// <inheritdoc />
    public async ValueTask CreateAsync(TenantId tenant, ContentEpochKey epoch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(epoch);
        var path = EpochPath(tenant, epoch.EpochId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(path)) throw new InvalidOperationException("That epoch already exists.");
            await WriteDurablyAsync(path, Encode(epoch), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<bool> TrySetCurrentAsync(TenantId tenant, Guid? expectedCurrent, Guid epochId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await ReadCurrentAsync(tenant, cancellationToken).ConfigureAwait(false) != expectedCurrent) return false;
            if (!File.Exists(EpochPath(tenant, epochId))) throw new InvalidOperationException("That epoch does not exist.");
            await WriteDurablyAsync(CurrentPath(tenant), Encoding.ASCII.GetBytes(epochId.ToString("N")), cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DestroyAsync(TenantId tenant, Guid epochId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = TenantDirectory(tenant);
            if (await ReadCurrentAsync(tenant, cancellationToken).ConfigureAwait(false) == epochId) File.Delete(CurrentPath(tenant));
            File.Delete(EpochPath(tenant, epochId));
            AfterDurableStep?.Invoke("destroy:file-deleted");
            if (Directory.Exists(directory)) NodeContentByteStore.SyncDirectory(directory);
            AfterDurableStep?.Invoke("destroy:directory-flushed");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<Guid?> ReadCurrentAsync(TenantId tenant, CancellationToken cancellationToken)
    {
        string text;
        try
        {
            text = await File.ReadAllTextAsync(CurrentPath(tenant), Encoding.ASCII, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }

        return Guid.TryParseExact(text, "N", out var current) ? current : throw new InvalidDataException("The current epoch file is unreadable.");
    }

    internal static async ValueTask WriteDurablyAsync(string path, byte[] contents, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(contents, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                NodeContentByteStore.FlushToDevice(stream);
                AfterFileStep(path, "file-flushed");
            }

            File.Move(temporary, path, overwrite: true);
            AfterFileStep(path, "renamed");
            NodeContentByteStore.SyncDirectory(directory);
            AfterFileStep(path, "directory-flushed");
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static void AfterFileStep(string path, string step)
    {
        var kind = path.EndsWith(Extension, StringComparison.Ordinal) ? "epoch"
            : Path.GetFileName(path) == CurrentName ? "current" : null;
        if (kind is not null) AfterDurableStep?.Invoke(kind + ":" + step);
    }

    // One file: magic, then the nonce, tag and wrapped-key lengths, then the three, so a truncated file never decodes.
    private static byte[] Encode(ContentEpochKey epoch)
    {
        var bytes = new byte[Magic.Length + 3 + epoch.WrapNonce.Length + epoch.WrapTag.Length + epoch.WrappedKey.Length];
        Magic.CopyTo(bytes, 0);
        var at = Magic.Length;
        bytes[at++] = checked((byte)epoch.WrapNonce.Length);
        bytes[at++] = checked((byte)epoch.WrapTag.Length);
        bytes[at++] = checked((byte)epoch.WrappedKey.Length);
        epoch.WrapNonce.CopyTo(bytes, at);
        at += epoch.WrapNonce.Length;
        epoch.WrapTag.CopyTo(bytes, at);
        at += epoch.WrapTag.Length;
        epoch.WrappedKey.CopyTo(bytes, at);
        return bytes;
    }

    private static ContentEpochKey Decode(Guid epochId, byte[] bytes)
    {
        var header = Magic.Length + 3;
        if (bytes.Length < header || !bytes.AsSpan(0, Magic.Length).SequenceEqual(Magic)) throw new InvalidDataException("The epoch key file is unreadable.");
        int nonce = bytes[Magic.Length], tag = bytes[Magic.Length + 1], key = bytes[Magic.Length + 2];
        if (bytes.Length != header + nonce + tag + key) throw new InvalidDataException("The epoch key file is unreadable.");
        return new(epochId, bytes[(header + nonce + tag)..], bytes[header..(header + nonce)], bytes[(header + nonce)..(header + nonce + tag)]);
    }

    // A tenant's directory is named by a hash of its id, so a tenant id never becomes a path.
    internal string TenantDirectory(TenantId tenant) =>
        Path.Combine(_root, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(tenant.Value))));

    private string EpochPath(TenantId tenant, Guid epochId) => Path.Combine(TenantDirectory(tenant), epochId.ToString("N") + Extension);

    private string CurrentPath(TenantId tenant) => Path.Combine(TenantDirectory(tenant), CurrentName);
}
