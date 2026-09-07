using Microsoft.Extensions.Options;

namespace Harborline.Api.Kernel.Buckets.Storage.Durability;

/// <summary>
/// PERS-2 (ADR 0137 D5b) — the reference <see cref="IDurabilityGuard"/>. Composes the never-evict registry (F0b),
/// the verify-before-evict possession verifier, and the (Phase-3-replaceable) eligibility predicate into a single
/// fail-closed shed decision.
/// </summary>
/// <remarks>
/// <para>
/// <b>Decision order (each step can only make the decision SAFER):</b>
/// </para>
/// <list type="number">
///   <item><b>Canonical pin (F0b).</b> If the record has a positive never-evict ref-count, refuse
///   (<see cref="ShedRefusalReason.PinnedCanonical"/>) regardless of replica count — a canonical copy is never
///   shed.</item>
///   <item><b>Verify possession.</b> Ask the verifier for the CONFIRMED replicas (it re-checks each, never
///   echoing a stale ledger).</item>
///   <item><b>Exclude SELF (F-Maj-2).</b> Drop the confirmed replica at <c>shedFrom</c> (the copy being shed),
///   matched by destination identity — the node cannot count its own about-to-be-removed copy toward N.</item>
///   <item><b>Filter to eligible.</b> Drop remaining confirmed replicas whose destination is not eligible to
///   count toward N (Phase-3 residency/data-class; Phase-1 stub keeps all).</item>
///   <item><b>Count DISTINCT failure domains.</b> Collapse co-located copies: two eligible confirmed replicas in
///   the same failure domain count once.</item>
///   <item><b>Threshold + co-location flag.</b> Permit iff distinct domains ≥ N. If there were ≥ N eligible
///   confirmed replicas but they collapse to &lt; N distinct domains, FLAG co-located redundancy and refuse
///   (P4/OQ8 — never silently counted as N≥2).</item>
/// </list>
/// </remarks>
public sealed class DurabilityGuard : IDurabilityGuard
{
    private readonly INeverEvictRegistry _pins;
    private readonly IReplicaPossessionVerifier _verifier;
    private readonly IDestinationEligibilityPredicate _eligibility;
    private readonly int _requiredIndependentReplicas;

    /// <summary>Construct from the never-evict registry, the possession verifier, the eligibility predicate, and N (options).</summary>
    public DurabilityGuard(
        INeverEvictRegistry pins,
        IReplicaPossessionVerifier verifier,
        IDestinationEligibilityPredicate eligibility,
        IOptions<DurabilityGuardOptions> options)
        : this(pins, verifier, eligibility, (options ?? throw new ArgumentNullException(nameof(options))).Value)
    {
    }

    /// <summary>Construct from an explicit options instance (used by tests and the chaos matrix).</summary>
    public DurabilityGuard(
        INeverEvictRegistry pins,
        IReplicaPossessionVerifier verifier,
        IDestinationEligibilityPredicate eligibility,
        DurabilityGuardOptions options)
    {
        _pins = pins ?? throw new ArgumentNullException(nameof(pins));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _eligibility = eligibility ?? throw new ArgumentNullException(nameof(eligibility));
        ArgumentNullException.ThrowIfNull(options);
        _requiredIndependentReplicas = options.RequiredIndependentReplicas;
    }

    /// <inheritdoc />
    public async ValueTask<ShedDecision> EvaluateShedAsync(DurableRef record, ReplicaDescriptor shedFrom, CancellationToken ct)
    {
        var n = _requiredIndependentReplicas;

        // 1. Canonical never-evict (F0b) — refuse independent of replica count.
        if (await _pins.IsPinnedAsync(record, ct).ConfigureAwait(false))
        {
            return new ShedDecision(
                CanShed: false,
                Reason: ShedRefusalReason.PinnedCanonical,
                RequiredIndependentReplicas: n,
                ConfirmedReplicaCount: 0,
                DistinctFailureDomainCount: 0,
                CoLocatedRedundancyFlagged: false,
                ConfirmedDomains: Array.Empty<FailureDomain>());
        }

        // 2. Verify possession (verify-before-evict — the verifier never trusts a stale ledger).
        var confirmed = await _verifier.VerifyPossessionAsync(record, ct).ConfigureAwait(false);

        // 3 + 4. Exclude SELF (the copy being shed, by destination identity) then filter to ELIGIBLE confirmed
        //         replicas (Phase-3 residency/data-class seam; Phase-1 stub keeps all). The self-exclusion is what
        //         makes the "≥ N remain ELSEWHERE" invariant structural (F-Maj-2).
        var eligibleDomains = new HashSet<FailureDomain>();
        var eligibleCount = 0;
        foreach (var replica in confirmed)
        {
            ct.ThrowIfCancellationRequested();
            if (string.Equals(replica.Destination.DestinationId, shedFrom.DestinationId, StringComparison.Ordinal))
            {
                continue;   // this is the copy being shed — it does not count toward "remains elsewhere".
            }
            if (await _eligibility.IsEligibleAsync(record, replica.Destination, ct).ConfigureAwait(false))
            {
                eligibleCount++;
                eligibleDomains.Add(replica.Destination.Domain);
            }
        }

        // 5. Count DISTINCT failure domains (co-located copies collapse to one).
        var distinctDomains = eligibleDomains.Count;

        // 6. Threshold + co-location flag.
        var canShed = distinctDomains >= n;
        // Co-located redundancy: enough eligible copies to *look* durable, but they do not span N domains.
        var coLocated = eligibleCount >= n && distinctDomains < n;

        ShedRefusalReason reason;
        if (canShed)
        {
            reason = ShedRefusalReason.None;
        }
        else if (coLocated)
        {
            reason = ShedRefusalReason.CoLocatedRedundancy;
        }
        else
        {
            reason = ShedRefusalReason.InsufficientIndependentReplicas;
        }

        return new ShedDecision(
            CanShed: canShed,
            Reason: reason,
            RequiredIndependentReplicas: n,
            ConfirmedReplicaCount: eligibleCount,
            DistinctFailureDomainCount: distinctDomains,
            CoLocatedRedundancyFlagged: coLocated,
            ConfirmedDomains: eligibleDomains.ToArray());
    }
}
