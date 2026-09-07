using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Foundation.Authorization;

/// <summary>Opaque user identifier presented by the canonical identity authority.</summary>
public readonly record struct PrincipalUserId
{
    /// <summary>Creates an opaque principal user identifier.</summary>
    public PrincipalUserId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A principal user id is required.", nameof(value));

        Value = value;
    }

    /// <summary>The unmodified identifier string.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// Lossless reference to the People pillar's string-backed PartyId. Values are never parsed or
/// coerced to <see cref="Guid"/>.
/// </summary>
public readonly record struct CanonicalPartyReference : IComparable<CanonicalPartyReference>
{
    /// <summary>Creates a lossless canonical Party reference.</summary>
    public CanonicalPartyReference(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A canonical Party reference is required.", nameof(value));

        Value = value;
    }

    /// <summary>The exact People PartyId string.</summary>
    public string Value { get; }

    /// <summary>Compares references with ordinal string semantics.</summary>
    public int CompareTo(CanonicalPartyReference other) =>
        StringComparer.Ordinal.Compare(Value, other.Value);

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>A verified live principal-to-Party binding under an exact tenant.</summary>
public sealed record CanonicalPartyBinding
{
    /// <summary>Creates a successful, live canonical Party binding.</summary>
    public CanonicalPartyBinding(
        TenantId verifiedTenant,
        PrincipalUserId principalUserId,
        CanonicalPartyReference partyId)
    {
        if (verifiedTenant == default)
            throw new ArgumentException("A verified tenant is required.", nameof(verifiedTenant));
        if (string.IsNullOrWhiteSpace(principalUserId.Value))
            throw new ArgumentException("A principal user id is required.", nameof(principalUserId));
        if (string.IsNullOrWhiteSpace(partyId.Value))
            throw new ArgumentException("A canonical Party reference is required.", nameof(partyId));

        VerifiedTenant = verifiedTenant;
        PrincipalUserId = principalUserId;
        PartyId = partyId;
    }

    /// <summary>The exact tenant predicate used to verify the binding.</summary>
    public TenantId VerifiedTenant { get; }

    /// <summary>The principal whose People-owned binding was verified.</summary>
    public PrincipalUserId PrincipalUserId { get; }

    /// <summary>The exact, string-backed People PartyId.</summary>
    public CanonicalPartyReference PartyId { get; }

    /// <summary>Successful bindings always prove that the Party row is present.</summary>
    public bool IsPresent => true;

    /// <summary>Successful bindings always prove that the Party row is not tombstoned.</summary>
    public bool IsTombstoned => false;
}
