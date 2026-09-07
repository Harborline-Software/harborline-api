using System.Collections.Concurrent;

using Harborline.Api.Foundation.Blobs;

namespace Harborline.Api.LocalNodeHost.Data.Forms;

/// <summary>
/// A minimal, single-process, content-addressed in-memory <see cref="IBlobStore"/>
/// fallback for the node-side wiring (ADR 0055). The kernel schema registry no
/// longer stores blobs (card 3776 — schema bytes live inline on the Schema
/// record); this store serves the remaining non-schema consumers (e.g.
/// org-branding assets) on the single-operator node v1, matching the in-memory
/// entity store the forms slice also uses.
/// </summary>
/// <remarks>
/// <para>
/// <b>Persistence posture (v1).</b> Blobs live for the process lifetime, NOT
/// across restart. The durable backing — a node-EF / SQLCipher-local-node.db
/// blob store (the analogue of the calendar / banking <c>NodeEf</c> overrides)
/// or a <see cref="FileSystemBlobStore"/> under the node data directory — is the
/// follow-up to this wiring slice. Schemas re-register deterministically (the CID
/// is the content hash), so a restart simply re-registers the same CID; no schema
/// identity churns.
/// </para>
/// <para>
/// Content-addressing gives free dedup + immutability: identical bytes always
/// produce the same CID (<see cref="Cid.FromBytes"/>), so a second put is a no-op.
/// </para>
/// </remarks>
internal sealed class NodeInMemoryBlobStore : IBlobStore
{
    private readonly ConcurrentDictionary<string, byte[]> _blobs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _pins = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public ValueTask<Cid> PutAsync(ReadOnlyMemory<byte> content, CancellationToken ct = default)
    {
        var cid = Cid.FromBytes(content.Span);
        // Idempotent: identical content → identical CID → same slot.
        _blobs.TryAdd(cid.Value, content.ToArray());
        return ValueTask.FromResult(cid);
    }

    /// <inheritdoc />
    public ValueTask<ReadOnlyMemory<byte>?> GetAsync(Cid cid, CancellationToken ct = default)
        => ValueTask.FromResult(_blobs.TryGetValue(cid.Value, out var bytes)
            ? (ReadOnlyMemory<byte>?)bytes
            : null);

    /// <inheritdoc />
    public ValueTask<bool> ExistsLocallyAsync(Cid cid, CancellationToken ct = default)
        => ValueTask.FromResult(_blobs.ContainsKey(cid.Value));

    /// <inheritdoc />
    public ValueTask PinAsync(Cid cid, CancellationToken ct = default)
    {
        _pins.TryAdd(cid.Value, 0);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask UnpinAsync(Cid cid, CancellationToken ct = default)
    {
        _pins.TryRemove(cid.Value, out _);
        return ValueTask.CompletedTask;
    }
}
