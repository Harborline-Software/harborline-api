using System.Collections.Immutable;

namespace Harborline.Api.Foundation.Authorization;

/// <summary>
/// Why the closure reader kept a binding out of <see cref="AuthorizationClosureSnapshot.Derivations"/>
/// (ticket 212 slice 2). A lapse reaches the gate as an absence, so the reader — the only place that knows
/// the difference — records it here rather than leaving the counterfactual to guess.
/// </summary>
public enum AuthorizationExclusionReason
{
    /// <summary>The binding's validity window closed before the decided instant.</summary>
    ValidityLapsed = 0,

    /// <summary>The binding's validity window had not opened at the decided instant.</summary>
    NotYetValid = 1,

    /// <summary>The grant behind the binding was revoked or is no longer active.</summary>
    GrantRevoked = 2,
}

/// <summary>A binding the closure reader excluded, with the reason it excluded it.</summary>
public sealed record AuthorizationExcludedBinding(
    AuthorizationAtomDerivation Binding,
    AuthorizationExclusionReason Reason);

public sealed record AuthorizationClosureSnapshot
{
    public AuthorizationClosureSnapshot(IReadOnlyList<AuthorizationAtomDerivation> derivations)
        : this(derivations, [])
    {
    }

    public AuthorizationClosureSnapshot(
        IReadOnlyList<AuthorizationAtomDerivation> derivations,
        IReadOnlyList<AuthorizationExcludedBinding> excluded)
    {
        ArgumentNullException.ThrowIfNull(derivations);
        ArgumentNullException.ThrowIfNull(excluded);
        Derivations = derivations.ToImmutableArray();
        Excluded = excluded.ToImmutableArray();
    }

    public IReadOnlyList<AuthorizationAtomDerivation> Derivations { get; }

    /// <summary>
    /// The bindings this read found on record but did not hand to the gate, each with its exclusion reason.
    /// The gate never decides from these; they exist so the counterfactual can name a lapse or a revocation
    /// that the gate could only see as an absence.
    /// </summary>
    public IReadOnlyList<AuthorizationExcludedBinding> Excluded { get; }
}

public interface IAuthorizationClosureSnapshotReader
{
    ValueTask<AuthorizationClosureSnapshot> ReadAsync(
        AuthorizationGateRequest request,
        CancellationToken ct = default);
}
