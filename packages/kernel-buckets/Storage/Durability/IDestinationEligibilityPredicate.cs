namespace Harborline.Api.Kernel.Buckets.Storage.Durability;

/// <summary>
/// PERS-2 cross-phase seam (the ordering trap the ADRs never sequence) — the durability guard counts "N
/// independent ELIGIBLE destinations," so it needs a predicate to exist BEFORE the eligibility ruleset does.
/// Phase 1 ships this interface + the trivial <see cref="AlwaysEligibleDestinationPredicate"/> stub; Phase 3
/// (PERS-4) replaces the stub with the real data-class / residency ruleset (ADR 0139 D5b). The INTERFACE is a
/// Phase-1 prerequisite; the RULESET is Phase-3 content.
/// </summary>
/// <remarks>
/// Eligibility gates COUNTING toward N, not possession itself: an ineligible destination may genuinely hold a
/// copy, but it does not count toward the durability threshold (e.g. a residency-ineligible region cannot be one
/// of the N independent homes for a residency-restricted record). Filtering on eligibility BEFORE the
/// distinct-domain count is what makes the guard compose with the Phase-3 residency gate without a re-cut.
/// </remarks>
public interface IDestinationEligibilityPredicate
{
    /// <summary>
    /// <c>true</c> iff a confirmed copy of <paramref name="record"/> at <paramref name="destination"/> may count
    /// toward the record's N independent-durability threshold.
    /// </summary>
    ValueTask<bool> IsEligibleAsync(DurableRef record, ReplicaDescriptor destination, CancellationToken ct);
}
