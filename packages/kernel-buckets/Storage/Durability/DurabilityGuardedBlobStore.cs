using Harborline.Api.Foundation.Blobs;

namespace Harborline.Api.Kernel.Buckets.Storage.Durability;

/// <summary>
/// PERS-2 — the blob-retention shed seam guard. Decorates an <see cref="IBlobStore"/> and gates
/// <see cref="UnpinAsync"/> through the <see cref="IDurabilityGuard"/>: removing a blob's retention mark is what
/// makes the backend eligible to GC it (see <see cref="IBlobStore.UnpinAsync"/> — "the backend may GC the blob at
/// any later time"), so an unpin can shed the last copy. This decorator refuses that fail-closed unless the guard
/// confirms the blob is durable elsewhere (or is a canonical never-evict copy, in which case unpin is also
/// refused — a canonical copy stays pinned).
/// </summary>
/// <remarks>
/// <para>
/// Every other operation (<see cref="PutAsync"/>, <see cref="GetAsync"/>, <see cref="ExistsLocallyAsync"/>,
/// <see cref="PinAsync"/>) passes straight through — only the shed seam (<see cref="UnpinAsync"/>) is gated. The
/// decorator composes with <c>EnvelopeBlobStore</c> (the C-3 at-rest cipher decorator) in either order.
/// </para>
/// <para>
/// <b>Build-not-wire (PERS-2 scope).</b> There is no live <see cref="UnpinAsync"/> caller today (the sole shed
/// path exercised is <see cref="IStorageBudgetManager.EvictLruAsync"/>); this decorator + its DI extension ship
/// the seam and its proof (tests), so when a GC/reclamation caller lands (Phase 2+) the guard is already in the
/// path rather than bolted on afterward.
/// </para>
/// </remarks>
public sealed class DurabilityGuardedBlobStore : IBlobStore
{
    private readonly IBlobStore _inner;
    private readonly IDurabilityGuard _guard;
    private readonly ILocalReplicaIdentity _localIdentity;

    /// <summary>Construct over the wrapped store, the durability guard, and this node's replica identity.</summary>
    public DurabilityGuardedBlobStore(IBlobStore inner, IDurabilityGuard guard, ILocalReplicaIdentity localIdentity)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _guard = guard ?? throw new ArgumentNullException(nameof(guard));
        _localIdentity = localIdentity ?? throw new ArgumentNullException(nameof(localIdentity));
    }

    /// <inheritdoc />
    public ValueTask<Cid> PutAsync(ReadOnlyMemory<byte> content, CancellationToken ct = default)
        => _inner.PutAsync(content, ct);

    /// <inheritdoc />
    public ValueTask<Cid> PutStreamingAsync(Stream content, CancellationToken ct = default)
        => _inner.PutStreamingAsync(content, ct);

    /// <inheritdoc />
    public ValueTask<ReadOnlyMemory<byte>?> GetAsync(Cid cid, CancellationToken ct = default)
        => _inner.GetAsync(cid, ct);

    /// <inheritdoc />
    public ValueTask<bool> ExistsLocallyAsync(Cid cid, CancellationToken ct = default)
        => _inner.ExistsLocallyAsync(cid, ct);

    /// <inheritdoc />
    public ValueTask PinAsync(Cid cid, CancellationToken ct = default) => _inner.PinAsync(cid, ct);

    /// <inheritdoc />
    /// <exception cref="DurabilityGuardRefusedException">
    /// The guard refused to shed this blob (it is a canonical pin, or fewer than N independent eligible confirmed
    /// replicas exist). The retention mark is KEPT.
    /// </exception>
    public async ValueTask UnpinAsync(Cid cid, CancellationToken ct = default)
    {
        var reference = DurableRef.ForBlob(cid);
        var decision = await _guard.EvaluateShedAsync(reference, _localIdentity.Self, ct).ConfigureAwait(false);
        if (!decision.CanShed)
        {
            throw new DurabilityGuardRefusedException(reference, decision);
        }
        await _inner.UnpinAsync(cid, ct).ConfigureAwait(false);
    }
}
