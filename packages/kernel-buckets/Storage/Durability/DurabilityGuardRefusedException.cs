namespace Harborline.Api.Kernel.Buckets.Storage.Durability;

/// <summary>
/// PERS-2 — thrown by <see cref="DurabilityGuardedBlobStore.UnpinAsync"/> when the durability guard REFUSES to
/// remove a blob's retention mark because doing so could shed the last durable copy. Fail-closed: the pin is
/// KEPT. A reclamation/GC caller should catch this, log the attached <see cref="Decision"/> (which explains
/// exactly why — canonical pin, insufficient independent replicas, or co-located redundancy), and SKIP the blob.
/// </summary>
public sealed class DurabilityGuardRefusedException : InvalidOperationException
{
    /// <summary>The full guard decision that caused the refusal.</summary>
    public ShedDecision Decision { get; }

    /// <summary>The reference that was refused.</summary>
    public DurableRef Record { get; }

    /// <summary>Construct with the refused reference and the guard's decision.</summary>
    public DurabilityGuardRefusedException(DurableRef record, ShedDecision decision)
        : base(BuildMessage(record, decision))
    {
        Record = record;
        Decision = decision;
    }

    private static string BuildMessage(DurableRef record, ShedDecision decision) =>
        $"Durability guard refused to unpin '{record.Value}' (reason: {decision.Reason}). "
        + $"Required N={decision.RequiredIndependentReplicas} independent failure domains; found "
        + $"{decision.DistinctFailureDomainCount} distinct domain(s) across {decision.ConfirmedReplicaCount} "
        + $"confirmed eligible replica(s)"
        + (decision.CoLocatedRedundancyFlagged
            ? " — the copies are co-located (redundant within a single failure domain), which is NOT counted as N≥2."
            : ".");
}
