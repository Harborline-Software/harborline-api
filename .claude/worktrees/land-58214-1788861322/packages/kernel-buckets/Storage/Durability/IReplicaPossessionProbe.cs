namespace Harborline.Api.Kernel.Buckets.Storage.Durability;

/// <summary>
/// PERS-2 — the VERIFY step of verify-before-evict. Given a raw ledger claim, decide whether the destination
/// CURRENTLY holds the record. This is the seam that separates "the ledger says X had it" from "X provably has it
/// now." A trust-the-ledger design would omit this; the durability guard requires it.
/// </summary>
/// <remarks>
/// <para>
/// The Phase-1 (MVP) reference implementation is <see cref="FreshnessWindowPossessionProbe"/> — it refuses to
/// treat a stale claim as a current confirmation. A future phase replaces it with a real cross-node possession
/// CHALLENGE (ask the destination to prove it can produce the bytes / the content hash over the sync transport),
/// which is the genuine "verify" — the freshness window is the honest MVP floor until that wire exists (see the
/// PR's honest-limitations note).
/// </para>
/// </remarks>
public interface IReplicaPossessionProbe
{
    /// <summary>
    /// Confirm (or refuse) that <paramref name="claim"/>'s destination still holds the record NOW. Only a
    /// <c>true</c> result lets the claim count toward the N≥2 durability threshold.
    /// </summary>
    ValueTask<bool> ConfirmAsync(ConfirmedReplica claim, CancellationToken ct);
}
