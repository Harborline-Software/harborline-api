namespace Harborline.Api.Kernel.Buckets.Storage.Durability;

/// <summary>
/// PERS-2 (ADR 0137 D5b) — the SAFETY CRUX. The single authority over the question "may this copy be shed?"
/// consulted at BOTH shed seams (record-body <see cref="IStorageBudgetManager.EvictLruAsync"/> and blob
/// <see cref="Harborline.Api.Foundation.Blobs.IBlobStore.UnpinAsync"/>). It permits shedding a copy ONLY when:
/// <list type="number">
///   <item>the record is NOT a canonical never-evict copy (<see cref="INeverEvictRegistry"/>, F0b); AND</item>
///   <item>≥ N independent failure domains hold a CONFIRMED, ELIGIBLE copy elsewhere — where possession is
///   VERIFIED (not read from a stale ledger) and a co-located "N=1-redundant" configuration is flagged and
///   refused, never silently counted as N≥2 (P4 / OQ8).</item>
/// </list>
/// It is fail-closed in the safe direction: when durability cannot be proven, it REFUSES to shed (keeps the copy).
/// </summary>
public interface IDurabilityGuard
{
    /// <summary>
    /// Evaluate whether the copy of <paramref name="record"/> held at <paramref name="shedFrom"/> may be shed, with
    /// the full evidence.
    /// </summary>
    /// <param name="record">The durably-held unit whose local copy is being considered for shedding.</param>
    /// <param name="shedFrom">
    /// PERS-2 F-Maj-2 — the destination whose copy is being shed (the LOCAL node). The guard EXCLUDES a confirmed
    /// replica at this destination from the count, so the "≥ N remain ELSEWHERE" invariant is structural rather than
    /// resting on an implicit caller contract to keep self out of the ledger. Exclusion is by destination identity
    /// (<see cref="ReplicaDescriptor.DestinationId"/>), so a co-located sibling at a DIFFERENT destination in the
    /// same failure domain still counts.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<ShedDecision> EvaluateShedAsync(DurableRef record, ReplicaDescriptor shedFrom, CancellationToken ct);
}
