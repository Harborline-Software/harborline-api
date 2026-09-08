namespace Harborline.Api.Kernel.Buckets.Storage.Durability;

/// <summary>
/// PERS-2 (ADR 0137 D15 observability) — a hook the eviction path notifies whenever the durability guard REFUSES
/// to shed a copy, so a silent N&lt;N-violation or a silent co-located-redundancy configuration is DETECTABLE in
/// production, not just in test. Purely observational: it never affects the shed decision.
/// </summary>
/// <remarks>
/// The default registration is <see cref="NullDurabilityEvictionObserver"/> (no-op). A host wires a real observer
/// (metrics/log) to surface the D15 durability signals: eviction-refusals, co-location flags, and N-confirmed
/// counts per record. Because it is observability (not a gate), a no-op default is safe — it cannot weaken the
/// guard.
/// </remarks>
public interface IDurabilityEvictionObserver
{
    /// <summary>Notified when the guard REFUSED to shed <paramref name="record"/>, with the full decision evidence.</summary>
    void OnShedRefused(DurableRef record, ShedDecision decision);
}

/// <summary>The no-op default <see cref="IDurabilityEvictionObserver"/>.</summary>
public sealed class NullDurabilityEvictionObserver : IDurabilityEvictionObserver
{
    /// <summary>The shared instance.</summary>
    public static readonly NullDurabilityEvictionObserver Instance = new();

    /// <inheritdoc />
    public void OnShedRefused(DurableRef record, ShedDecision decision) { /* no-op */ }
}
