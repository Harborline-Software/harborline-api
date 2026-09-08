namespace Harborline.Api.Kernel.Buckets.Storage.Durability;

/// <summary>
/// PERS-2 — the record of which destinations have (at some point) reported holding a copy of a record. This is
/// the RAW claim store — it is NOT the source of truth the guard trusts. The guard never reads this directly; it
/// goes through <see cref="IReplicaPossessionVerifier"/>, which re-CONFIRMS each ledger claim before counting it
/// (verify-before-evict, not trust-the-ledger). A ledger entry means "a destination once said it had this," which
/// is precisely the stale/forgeable signal the guard must re-validate rather than trust.
/// </summary>
/// <remarks>
/// <b>Async by contract (PERS-2 F-Maj-3).</b> The surface is <see cref="System.Threading.Tasks.ValueTask"/>-returning
/// so a DURABLE / cross-node claim store can persist/query without sync-over-async on the async eviction path. The
/// in-memory reference impl completes synchronously.
/// </remarks>
public interface IReplicaPossessionLedger
{
    /// <summary>Record (upsert) a possession claim for a destination. A newer claim for the same destination replaces the older one.</summary>
    ValueTask RecordAsync(ConfirmedReplica claim, CancellationToken ct = default);

    /// <summary>All currently-recorded possession claims for <paramref name="record"/> (may be empty). These are UNVERIFIED.</summary>
    ValueTask<IReadOnlyList<ConfirmedReplica>> ListAsync(DurableRef record, CancellationToken ct = default);

    /// <summary>Forget a destination's claim for a record (e.g. the destination reported eviction). Returns whether anything was removed.</summary>
    ValueTask<bool> ForgetAsync(DurableRef record, string destinationId, CancellationToken ct = default);
}
