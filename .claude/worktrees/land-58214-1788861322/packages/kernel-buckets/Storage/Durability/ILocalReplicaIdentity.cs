namespace Harborline.Api.Kernel.Buckets.Storage.Durability;

/// <summary>
/// PERS-2 F-Maj-2 — the identity of the LOCAL node, i.e. WHOSE copy the shed seams are about to shed. The durability
/// guard excludes this copy from the "≥N remain elsewhere" count so the node cannot count its OWN about-to-be-removed
/// copy toward the durability threshold (the self-inclusion data-loss hazard the PERS-1/PERS-2 deep review flagged).
/// </summary>
/// <remarks>
/// The record-eviction seam (<see cref="IStorageBudgetManager.EvictLruAsync"/>) and the blob-unpin seam
/// (<see cref="DurabilityGuardedBlobStore"/>) both shed the LOCAL node's copy, so both pass this identity as the
/// guard's <c>shedFrom</c>. A deployment that records self-possession into the ledger MUST wire its REAL node
/// identity here so the exclusion matches; a host that never records self-possession can leave the default (the
/// exclusion is then a no-op — nothing self to exclude).
/// </remarks>
public interface ILocalReplicaIdentity
{
    /// <summary>This node's replica descriptor (its destination id + failure domain).</summary>
    ReplicaDescriptor Self { get; }
}

/// <summary>PERS-2 — the default <see cref="ILocalReplicaIdentity"/> holding an explicit descriptor.</summary>
public sealed class LocalReplicaIdentity : ILocalReplicaIdentity
{
    /// <summary>Construct from this node's descriptor.</summary>
    public LocalReplicaIdentity(ReplicaDescriptor self)
    {
        self.EnsureValid();
        Self = self;
    }

    /// <inheritdoc />
    public ReplicaDescriptor Self { get; }

    /// <summary>
    /// The conservative default for a host that has NOT wired an eviction path with a real node identity: a stable
    /// sentinel descriptor that matches no real destination, so the guard's self-exclusion is a harmless no-op. A
    /// deployment that records self-possession MUST replace this (register its real <see cref="ILocalReplicaIdentity"/>)
    /// so its own copy is correctly excluded from the durability count.
    /// </summary>
    public static ILocalReplicaIdentity Unconfigured { get; } = new LocalReplicaIdentity(
        new ReplicaDescriptor("local-node-unconfigured", new FailureDomain("local-node-unconfigured")));
}
