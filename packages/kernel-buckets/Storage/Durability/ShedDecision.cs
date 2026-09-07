namespace Harborline.Api.Kernel.Buckets.Storage.Durability;

/// <summary>PERS-2 — why the durability guard permitted or refused shedding a copy.</summary>
public enum ShedRefusalReason
{
    /// <summary>The guard PERMITTED the shed (<see cref="ShedDecision.CanShed"/> is <c>true</c>).</summary>
    None = 0,

    /// <summary>Refused: the record is a canonical never-evict copy (positive <see cref="INeverEvictRegistry"/> ref-count). F0b.</summary>
    PinnedCanonical = 1,

    /// <summary>Refused: fewer than N independent, eligible, CONFIRMED replicas exist elsewhere.</summary>
    InsufficientIndependentReplicas = 2,

    /// <summary>
    /// Refused AND FLAGGED: there ARE ≥ N confirmed eligible replicas, but they collapse to fewer than N distinct
    /// failure domains — a co-located "N=1-redundant" configuration that must never be silently counted as N≥2
    /// (P4 / OQ8).
    /// </summary>
    CoLocatedRedundancy = 3,
}

/// <summary>
/// PERS-2 — the durability guard's decision about a single "shed a copy" request, with the full evidence so the
/// caller (and the observability surface, ADR 0137 D15) can see WHY, not just yes/no.
/// </summary>
/// <param name="CanShed">
/// <c>true</c> iff the copy may be shed. When <c>true</c>, ≥ <paramref name="RequiredIndependentReplicas"/>
/// independent eligible confirmed replicas exist elsewhere, so shedding this copy cannot lose the last copy.
/// </param>
/// <param name="Reason">Why (see <see cref="ShedRefusalReason"/>).</param>
/// <param name="RequiredIndependentReplicas">The configured N.</param>
/// <param name="ConfirmedReplicaCount">How many confirmed eligible replicas were counted (before the distinct-domain reduction).</param>
/// <param name="DistinctFailureDomainCount">How many DISTINCT failure domains those confirmed eligible replicas occupy.</param>
/// <param name="CoLocatedRedundancyFlagged">
/// <c>true</c> when redundancy exists (≥ N confirmed eligible replicas) but it collapses to fewer than N distinct
/// domains — the explicit P4/OQ8 flag. When set, the configuration is NOT counted as N≥2.
/// </param>
/// <param name="ConfirmedDomains">The distinct failure domains counted (for diagnostics/observability).</param>
public sealed record ShedDecision(
    bool CanShed,
    ShedRefusalReason Reason,
    int RequiredIndependentReplicas,
    int ConfirmedReplicaCount,
    int DistinctFailureDomainCount,
    bool CoLocatedRedundancyFlagged,
    IReadOnlyList<FailureDomain> ConfirmedDomains);
