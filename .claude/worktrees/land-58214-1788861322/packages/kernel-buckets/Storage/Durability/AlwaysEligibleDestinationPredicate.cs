namespace Harborline.Api.Kernel.Buckets.Storage.Durability;

/// <summary>
/// PERS-2 — the trivial Phase-1 <see cref="IDestinationEligibilityPredicate"/> stub: EVERY destination is eligible
/// to count toward N. This lets the durability guard count independent replicas before the real
/// data-class/residency ruleset exists. Phase 3 (PERS-4) replaces this registration with the ADR 0139 D5b
/// ruleset; nothing else about the guard changes.
/// </summary>
public sealed class AlwaysEligibleDestinationPredicate : IDestinationEligibilityPredicate
{
    /// <inheritdoc />
    public ValueTask<bool> IsEligibleAsync(DurableRef record, ReplicaDescriptor destination, CancellationToken ct)
        => ValueTask.FromResult(true);
}
