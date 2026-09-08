namespace Harborline.Api.Kernel.Buckets.Storage.Durability;

/// <summary>
/// PERS-2 — configuration for the <see cref="DurabilityGuard"/>.
/// </summary>
public sealed class DurabilityGuardOptions
{
    /// <summary>The minimum number of INDEPENDENT failure domains (N) that must hold a confirmed eligible copy before a copy may be shed.</summary>
    public const int MinimumSafeReplicas = 2;

    private int _requiredIndependentReplicas = MinimumSafeReplicas;

    /// <summary>
    /// N — the required number of DISTINCT independent failure domains with a confirmed eligible copy. Defaults to
    /// <see cref="MinimumSafeReplicas"/> (2). Setting it below 2 is rejected: a single-domain target defeats the
    /// guard's whole purpose (there would be no independence), and the P4/OQ8 rule requires a co-located
    /// N=1-redundant configuration to be FLAGGED, never silently accepted. A deployment that wants stronger
    /// durability may raise N (3, 4, …).
    /// </summary>
    public int RequiredIndependentReplicas
    {
        get => _requiredIndependentReplicas;
        set
        {
            if (value < MinimumSafeReplicas)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(RequiredIndependentReplicas),
                    value,
                    $"The durability guard requires N ≥ {MinimumSafeReplicas} independent failure domains; a "
                    + "lower value would let a single (or co-located) copy authorize shedding the last copy.");
            }
            _requiredIndependentReplicas = value;
        }
    }
}
