using System.Collections.Concurrent;

namespace Harborline.Api.Kernel.Buckets.Storage.Durability;

/// <summary>
/// PERS-2 F0b — the in-memory reference <see cref="INeverEvictRegistry"/>. Thread-safe ref-counting keyed by
/// <see cref="DurableRef"/>. A persistent (durable, restart-surviving) registry is a later phase; the interface is
/// what the guard depends on, so the durable implementation is a drop-in replacement.
/// </summary>
/// <remarks>
/// The counts are held as a <see cref="ConcurrentDictionary{TKey,TValue}"/> with atomic add/update, so concurrent
/// pins/unpins on the same record never lose a count. A zero-count entry is removed so <see cref="RefCountAsync"/>
/// of an unpinned record allocates nothing. The interface is async (F-Maj-3) but this reference impl completes
/// synchronously — <see cref="ValueTask"/> makes that allocation-free.
/// </remarks>
public sealed class InMemoryNeverEvictRegistry : INeverEvictRegistry
{
    private readonly ConcurrentDictionary<string, long> _counts = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public ValueTask<long> PinAsync(DurableRef record, CancellationToken ct = default)
        => ValueTask.FromResult(_counts.AddOrUpdate(record.Value, 1, static (_, c) => c + 1));

    /// <inheritdoc />
    public ValueTask<long> UnpinAsync(DurableRef record, CancellationToken ct = default)
    {
        // AddOrUpdate with a floor at zero, then prune a zeroed entry so it does not leak.
        var updated = _counts.AddOrUpdate(record.Value, 0, static (_, c) => c > 0 ? c - 1 : 0);
        if (updated == 0)
        {
            // Remove only if still zero (a concurrent Pin may have raced it back up).
            ((System.Collections.Generic.ICollection<System.Collections.Generic.KeyValuePair<string, long>>)_counts)
                .Remove(new System.Collections.Generic.KeyValuePair<string, long>(record.Value, 0));
        }
        return ValueTask.FromResult(updated);
    }

    /// <inheritdoc />
    public ValueTask<long> RefCountAsync(DurableRef record, CancellationToken ct = default)
        => ValueTask.FromResult(_counts.TryGetValue(record.Value, out var c) ? c : 0);

    /// <inheritdoc />
    public ValueTask<bool> IsPinnedAsync(DurableRef record, CancellationToken ct = default)
        => ValueTask.FromResult((_counts.TryGetValue(record.Value, out var c) ? c : 0) > 0);
}
