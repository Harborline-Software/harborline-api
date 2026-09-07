namespace Harborline.Api.Kernel.Buckets.Storage.Durability;

/// <summary>
/// PERS-2 F0b (re-sequenced from PERS-1) — the ref-counted NEVER-EVICT registry for CANONICAL records. A record
/// with a positive ref-count is a canonical copy that MUST NOT be shed under any circumstance — the durability
/// guard refuses eviction/unpin of a pinned record independent of how many independent replicas are confirmed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why ref-COUNTED, not a boolean pin.</b> A canonical record may be held for several independent reasons at
/// once (it is the storage-role node's canonical copy AND a legal hold references it AND a retention rule pins
/// it). A single boolean would let the first releaser strand the others' guarantee. A ref-count releases the
/// never-evict guarantee only when the LAST holder releases it (count returns to zero) — the same
/// "don't lose the last copy" concern as the N≥2 guard, expressed for canonical data.
/// </para>
/// <para>
/// <b>Async by contract (PERS-2 F-Maj-3).</b> The surface is <see cref="System.Threading.Tasks.ValueTask"/>-returning
/// so a DURABLE / cross-node implementation (the first will be the ADR-0142 legal-hold registry) can persist the
/// counts without sync-over-async on the async eviction path. The in-memory reference impl completes synchronously.
/// </para>
/// </remarks>
public interface INeverEvictRegistry
{
    /// <summary>Increment the never-evict ref-count for <paramref name="record"/>. Returns the new count (≥ 1).</summary>
    ValueTask<long> PinAsync(DurableRef record, CancellationToken ct = default);

    /// <summary>
    /// Decrement the never-evict ref-count for <paramref name="record"/>. Never drops below zero (a decrement of an
    /// unpinned record is a no-op returning 0). Returns the new count. When the count reaches 0 the record is no
    /// longer canonical-pinned and becomes eligible for the N≥2 durability evaluation like any other record.
    /// </summary>
    ValueTask<long> UnpinAsync(DurableRef record, CancellationToken ct = default);

    /// <summary>The current never-evict ref-count for <paramref name="record"/> (0 when unpinned).</summary>
    ValueTask<long> RefCountAsync(DurableRef record, CancellationToken ct = default);

    /// <summary><c>true</c> iff <paramref name="record"/> has a positive never-evict ref-count.</summary>
    ValueTask<bool> IsPinnedAsync(DurableRef record, CancellationToken ct = default);
}
