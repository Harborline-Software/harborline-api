namespace Harborline.Api.Kernel.Buckets.Storage.Durability;

/// <summary>
/// PERS-2 — the verify-before-evict primitive. Returns the set of destinations that have a CONFIRMED current
/// possession of a record. This is the ONLY possession input the durability guard trusts: it never reads the raw
/// <see cref="IReplicaPossessionLedger"/> directly, because a ledger entry is a stale/forgeable claim
/// ("was replicated"), not proof of current possession.
/// </summary>
/// <remarks>
/// <para>
/// A conforming implementation MUST actively CONFIRM each destination (via an <see cref="IReplicaPossessionProbe"/>
/// or a real cross-node challenge) and return only confirmed ones — it MUST NOT echo the ledger unverified.
/// </para>
/// <para>
/// <b>Self is NOT special here (PERS-2 F-Maj-2).</b> The verifier reports possession at ALL destinations it can
/// confirm, INCLUDING the local node if it holds a copy. Excluding the copy being shed (so the node cannot count
/// its own about-to-be-removed copy toward N) is the DURABILITY GUARD's job, via the <c>shedFrom</c> argument to
/// <see cref="IDurabilityGuard.EvaluateShedAsync"/> — it is a structural guard responsibility, never an implicit
/// caller contract on this verifier. The guard excludes <c>shedFrom</c> by destination identity before counting.
/// </para>
/// </remarks>
public interface IReplicaPossessionVerifier
{
    /// <summary>The destinations confirmed to currently hold <paramref name="record"/> (may be empty).</summary>
    ValueTask<IReadOnlyList<ConfirmedReplica>> VerifyPossessionAsync(DurableRef record, CancellationToken ct);
}
