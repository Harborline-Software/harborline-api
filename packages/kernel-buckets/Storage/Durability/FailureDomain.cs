namespace Harborline.Api.Kernel.Buckets.Storage.Durability;

/// <summary>
/// PERS-2 (ADR 0137 D5b / P4 / OQ8) — the INDEPENDENCE key of a replica destination. Two destinations are
/// considered independent failure domains iff their <see cref="FailureDomain"/> values DIFFER.
/// </summary>
/// <remarks>
/// <para>
/// The durability guarantee is "N copies that do not fail together." A failure domain is the coarsest locus of
/// correlated failure a deployment chooses to model — a distinct host, a distinct storage medium, a distinct
/// power/network domain, or a distinct geographic site. The guard counts DISTINCT failure domains among the
/// confirmed eligible replicas; two copies that share a failure domain (e.g. two directories on one disk, two
/// processes on one host) count as ONE domain, never two. This is what stops a co-located "N=1-redundant"
/// configuration from being silently mistaken for N≥2 (P4 / OQ8).
/// </para>
/// <para>
/// The value is normalized to an <see cref="StringComparer.Ordinal"/> trimmed form; equality is ordinal. What
/// string a deployment assigns (host id, site code, medium id, or a composite) is a wiring concern — the guard
/// only reasons about equality/inequality of the assigned values.
/// </para>
/// </remarks>
public readonly record struct FailureDomain
{
    /// <summary>Construct a failure domain from a non-empty identity string (trimmed).</summary>
    public FailureDomain(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value.Trim();
    }

    /// <summary>The normalized failure-domain identity. Ordinal equality decides independence.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public bool Equals(FailureDomain other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value ?? string.Empty);

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// PERS-2 — a replica destination: WHO holds (or could hold) a copy, and in WHICH failure domain. The guard
/// counts distinct <see cref="Domain"/> values across confirmed eligible destinations toward N.
/// </summary>
/// <param name="DestinationId">
/// The destination's stable identity (a node id / storage-node id). Distinct destinations may still share a
/// failure domain (that is exactly the co-located case the guard flags).
/// </param>
/// <param name="Domain">The destination's failure-domain independence key.</param>
public readonly record struct ReplicaDescriptor(string DestinationId, FailureDomain Domain)
{
    /// <summary>Validate that the destination id is non-empty (the domain validates itself).</summary>
    public ReplicaDescriptor EnsureValid()
    {
        ArgumentException.ThrowIfNullOrEmpty(DestinationId);
        return this;
    }
}

/// <summary>
/// PERS-2 — a possession of <paramref name="Record"/> that a <paramref name="Destination"/> has CONFIRMED at
/// <paramref name="ConfirmedAt"/>. Produced only by <see cref="IReplicaPossessionVerifier"/> (verify-before-evict);
/// the guard never trusts an unconfirmed claim.
/// </summary>
/// <param name="Record">The durably-held unit this confirmation is about.</param>
/// <param name="Destination">The destination that holds it, with its failure domain.</param>
/// <param name="ConfirmedAt">
/// The instant possession was last confirmed. A verifier uses this for freshness (a stale confirmation is not a
/// current confirmation — verify-before-evict, not trust-the-ledger).
/// </param>
public readonly record struct ConfirmedReplica(
    DurableRef Record,
    ReplicaDescriptor Destination,
    DateTimeOffset ConfirmedAt);
