using System.Collections.Concurrent;
using System.Text.Json;

namespace Harborline.Api.Kernel.Sync.Restore;

/// <summary>Durable holder-side evidence of each replica's highest published position.</summary>
public interface IPublishedPositionStore
{
    /// <summary>Returns the highest position observed for <paramref name="nodeId"/>, or zero.</summary>
    ValueTask<ulong> GetAsync(string nodeId, CancellationToken ct = default);

    /// <summary>Advances the observed position without permitting it to move backwards.</summary>
    ValueTask AdvanceAsync(string nodeId, ulong position, CancellationToken ct = default);

    /// <summary>Atomically reserves a position above both the stored value and the supplied floor.</summary>
    ValueTask<ulong> ReserveNextAsync(
        string nodeId,
        ulong minimumExclusive,
        CancellationToken ct = default);
}

/// <summary>File-backed published-position evidence retained by a replica holder.</summary>
public sealed class FilePublishedPositionStore : IPublishedPositionStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> FileLocks =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly string _path;
    private readonly SemaphoreSlim _fileLock;

    /// <summary>Creates a store rooted in a holder-owned durable directory.</summary>
    public FilePublishedPositionStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _path = Path.GetFullPath(Path.Combine(directory, "published-positions.json"));
        _fileLock = FileLocks.GetOrAdd(_path, static _ => new SemaphoreSlim(1, 1));
    }

    /// <inheritdoc />
    public async ValueTask<ulong> GetAsync(string nodeId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        await _fileLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var positions = await ReadAsync(ct).ConfigureAwait(false);
            return positions.GetValueOrDefault(nodeId);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask AdvanceAsync(
        string nodeId,
        ulong position,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        await _fileLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var positions = await ReadAsync(ct).ConfigureAwait(false);
            var current = positions.GetValueOrDefault(nodeId);
            if (position < current)
            {
                throw new InvalidOperationException(
                    $"Published position for '{nodeId}' cannot move backwards from {current} to {position}.");
            }

            if (position == current)
            {
                return;
            }

            positions[nodeId] = position;
            await WriteAsync(positions, ct).ConfigureAwait(false);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<ulong> ReserveNextAsync(
        string nodeId,
        ulong minimumExclusive,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        await _fileLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var positions = await ReadAsync(ct).ConfigureAwait(false);
            var floor = Math.Max(positions.GetValueOrDefault(nodeId), minimumExclusive);
            if (floor == ulong.MaxValue)
            {
                throw new OverflowException($"Published position for '{nodeId}' is exhausted.");
            }

            var next = floor + 1;
            positions[nodeId] = next;
            await WriteAsync(positions, ct).ConfigureAwait(false);
            return next;
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private async ValueTask<Dictionary<string, ulong>> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(_path))
        {
            return new Dictionary<string, ulong>(StringComparer.Ordinal);
        }

        await using var stream = new FileStream(
            _path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous);
        return await JsonSerializer.DeserializeAsync<Dictionary<string, ulong>>(stream, cancellationToken: ct)
            .ConfigureAwait(false)
            ?? new Dictionary<string, ulong>(StringComparer.Ordinal);
    }

    private async ValueTask WriteAsync(
        Dictionary<string, ulong> positions,
        CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
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
                await JsonSerializer.SerializeAsync(stream, positions, cancellationToken: ct)
                    .ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }

            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}

/// <summary>Allocates the monotonic sequence carried by outbound delta frames.</summary>
public interface IOutboundSequenceAllocator
{
    /// <summary>Durably reserves the next sequence for <paramref name="nodeId"/>.</summary>
    ValueTask<ulong> ReserveNextAsync(string nodeId, CancellationToken ct = default);
}

/// <summary>Allocates outbound sequences from durable published-position evidence.</summary>
public sealed class PublishedPositionSequenceAllocator(IPublishedPositionStore positions)
    : IOutboundSequenceAllocator
{
    private readonly IPublishedPositionStore _positions =
        positions ?? throw new ArgumentNullException(nameof(positions));

    /// <inheritdoc />
    public ValueTask<ulong> ReserveNextAsync(string nodeId, CancellationToken ct = default) =>
        _positions.ReserveNextAsync(nodeId, minimumExclusive: 0, ct);
}

internal sealed class InMemoryOutboundSequenceAllocator : IOutboundSequenceAllocator
{
    private readonly object _lock = new();
    private readonly Dictionary<string, ulong> _positions = new(StringComparer.Ordinal);

    public ValueTask<ulong> ReserveNextAsync(string nodeId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ct.ThrowIfCancellationRequested();
        lock (_lock)
        {
            var current = _positions.GetValueOrDefault(nodeId);
            if (current == ulong.MaxValue)
            {
                throw new OverflowException($"Published position for '{nodeId}' is exhausted.");
            }

            var next = current + 1;
            _positions[nodeId] = next;
            return ValueTask.FromResult(next);
        }
    }
}

/// <summary>Admission outcomes for a replica returning to its holders.</summary>
public enum ReplicaReturnDisposition
{
    /// <summary>The returning replica is at or ahead of its last published position.</summary>
    AdmitExistingIdentity,

    /// <summary>The returning replica moved backwards and must re-host.</summary>
    RestoreRequired,
}

/// <summary>Result of comparing a returning replica with durable holder evidence.</summary>
public sealed record ReplicaReturnDecision(
    ReplicaReturnDisposition Disposition,
    ulong LastPublishedPosition);

/// <summary>Detects replica rollback against evidence retained by a canonical holder.</summary>
public sealed class ReplicaReturnDetector(IPublishedPositionStore positions)
{
    private readonly IPublishedPositionStore _positions =
        positions ?? throw new ArgumentNullException(nameof(positions));

    /// <summary>Classifies a returning replica before its clock or deltas are admitted.</summary>
    public async ValueTask<ReplicaReturnDecision> EvaluateAsync(
        string nodeId,
        ulong returningPosition,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        var lastPublished = await _positions.GetAsync(nodeId, ct).ConfigureAwait(false);
        return new ReplicaReturnDecision(
            returningPosition < lastPublished
                ? ReplicaReturnDisposition.RestoreRequired
                : ReplicaReturnDisposition.AdmitExistingIdentity,
            lastPublished);
    }
}
