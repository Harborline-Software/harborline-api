using Harborline.Api.Kernel.Buckets.Storage.Durability;

namespace Harborline.Api.Kernel.Buckets.Storage;

/// <summary>
/// In-memory reference <see cref="IStorageBudgetManager"/>. Suitable for tests and for the
/// initial non-persistent node host. A persistent manager (SQLite-backed) is future work.
/// </summary>
/// <remarks>
/// <para>
/// <b>PERS-2 (ADR 0137 D5b) — durability-gated eviction.</b> <see cref="EvictLruAsync"/> no longer sheds a record
/// body on LRU + lazy-bucket eligibility alone. Each candidate must ALSO pass the injected
/// <see cref="IDurabilityGuard"/>: a record is shed only if it is not a canonical never-evict copy AND ≥ N
/// independent failure domains hold a confirmed, eligible copy elsewhere (verify-before-evict). A candidate the
/// guard refuses is KEPT (skipped), and the refusal is surfaced to the <see cref="IDurabilityEvictionObserver"/>
/// (D15). The guard is a REQUIRED dependency — there is deliberately no guard-less constructor, so a host cannot
/// silently ship an ungated eviction path (the wire-the-durable-gate discipline).
/// </para>
/// </remarks>
public sealed class InMemoryStorageBudgetManager : IStorageBudgetManager
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Bucket, string Record), TrackedRecord> _tracked = new();
    private readonly IBucketRegistry _registry;
    private readonly IDurabilityGuard _durabilityGuard;
    private readonly ILocalReplicaIdentity _localIdentity;
    private readonly IDurabilityEvictionObserver _observer;
    private readonly StorageBudget _budget;

    /// <summary>Construct with a default (10 GB) budget and the no-op eviction observer.</summary>
    public InMemoryStorageBudgetManager(
        IBucketRegistry registry, IDurabilityGuard durabilityGuard, ILocalReplicaIdentity localIdentity)
        : this(registry, durabilityGuard, localIdentity, new StorageBudget(), NullDurabilityEvictionObserver.Instance) { }

    /// <summary>Construct with a caller-supplied budget (e.g. for tests with a tight limit) and the no-op observer.</summary>
    public InMemoryStorageBudgetManager(
        IBucketRegistry registry, IDurabilityGuard durabilityGuard, ILocalReplicaIdentity localIdentity, StorageBudget budget)
        : this(registry, durabilityGuard, localIdentity, budget, NullDurabilityEvictionObserver.Instance) { }

    /// <summary>Construct with a caller-supplied budget and a durability-eviction observer (D15 observability).</summary>
    public InMemoryStorageBudgetManager(
        IBucketRegistry registry,
        IDurabilityGuard durabilityGuard,
        ILocalReplicaIdentity localIdentity,
        StorageBudget budget,
        IDurabilityEvictionObserver observer)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _durabilityGuard = durabilityGuard ?? throw new ArgumentNullException(nameof(durabilityGuard));
        _localIdentity = localIdentity ?? throw new ArgumentNullException(nameof(localIdentity));
        _budget = budget ?? throw new ArgumentNullException(nameof(budget));
        _observer = observer ?? throw new ArgumentNullException(nameof(observer));
    }

    /// <inheritdoc />
    public StorageBudget Current => _budget;

    /// <inheritdoc />
    public void Track(TrackedRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrEmpty(record.BucketName);
        ArgumentException.ThrowIfNullOrEmpty(record.RecordId);
        if (record.ContentLengthBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(record), "ContentLengthBytes must be >= 0.");
        }

        lock (_gate)
        {
            var key = (record.BucketName, record.RecordId);
            if (_tracked.TryGetValue(key, out var existing))
            {
                _budget.CurrentBytes -= existing.ContentLengthBytes;
            }
            _tracked[key] = record;
            _budget.CurrentBytes += record.ContentLengthBytes;
        }
    }

    /// <inheritdoc />
    public bool Untrack(string bucketName, string recordId)
    {
        ArgumentException.ThrowIfNullOrEmpty(bucketName);
        ArgumentException.ThrowIfNullOrEmpty(recordId);

        lock (_gate)
        {
            var key = (bucketName, recordId);
            if (!_tracked.TryGetValue(key, out var existing))
            {
                return false;
            }
            _budget.CurrentBytes -= existing.ContentLengthBytes;
            _tracked.Remove(key);
            return true;
        }
    }

    /// <inheritdoc />
    public void TouchAccess(string bucketName, string recordId, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrEmpty(bucketName);
        ArgumentException.ThrowIfNullOrEmpty(recordId);

        lock (_gate)
        {
            var key = (bucketName, recordId);
            if (_tracked.TryGetValue(key, out var existing))
            {
                _tracked[key] = existing with { LastAccessed = now };
            }
        }
    }

    /// <inheritdoc />
    public async Task<long> EvictLruAsync(long bytesToReclaim, CancellationToken ct)
    {
        if (bytesToReclaim <= 0)
        {
            return 0L;
        }

        // 1. Snapshot the lazy-bucket candidates, oldest access first, UNDER the lock (fast, no I/O). We must not
        //    hold the lock across the async durability-guard evaluation (the guard verifies possession, which is
        //    I/O in a real deployment), so we snapshot, release, evaluate, then re-acquire to remove.
        List<TrackedRecord> candidates;
        lock (_gate)
        {
            candidates = _tracked.Values
                .Where(r => IsLazyBucket(r.BucketName))
                .OrderBy(r => r.LastAccessed)
                .ToList();
        }

        // 2. Evaluate the durability guard per candidate (outside the lock). Collect the shed-approved ones until
        //    the target is (optimistically) met by their tracked sizes; a refused candidate is KEPT and surfaced.
        var approved = new List<TrackedRecord>();
        long approvedBytes = 0;
        foreach (var candidate in candidates)
        {
            if (approvedBytes >= bytesToReclaim)
            {
                break;
            }
            ct.ThrowIfCancellationRequested();

            var reference = DurableRef.ForRecord(candidate.BucketName, candidate.RecordId);
            // The shed subject is THIS node's copy — exclude it from the "remains elsewhere" count (F-Maj-2).
            var decision = await _durabilityGuard
                .EvaluateShedAsync(reference, _localIdentity.Self, ct).ConfigureAwait(false);
            if (decision.CanShed)
            {
                approved.Add(candidate);
                approvedBytes += candidate.ContentLengthBytes;
            }
            else
            {
                // The guard refused — the copy is KEPT. Surface it (D15) so a durability shortfall is observable.
                _observer.OnShedRefused(reference, decision);
            }
        }

        // 3. Remove the approved candidates UNDER the lock, re-checking each is still tracked with the same size
        //    (a concurrent Track/Untrack/TouchAccess may have changed it since the snapshot).
        long reclaimed = 0;
        lock (_gate)
        {
            foreach (var candidate in approved)
            {
                if (reclaimed >= bytesToReclaim)
                {
                    break;
                }
                var key = (candidate.BucketName, candidate.RecordId);
                if (_tracked.TryGetValue(key, out var current)
                    && current.ContentLengthBytes == candidate.ContentLengthBytes)
                {
                    _tracked.Remove(key);
                    _budget.CurrentBytes -= current.ContentLengthBytes;
                    reclaimed += current.ContentLengthBytes;
                }
            }
        }
        return reclaimed;
    }

    private bool IsLazyBucket(string bucketName)
    {
        var def = _registry.Find(bucketName);
        return def is not null && def.Replication == ReplicationMode.Lazy;
    }
}
