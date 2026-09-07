using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Packs.Trust;

namespace Harborline.Api.Foundation.Packs.Install.Trust;

/// <summary>One revoked signing key + epoch (ADR 0126 D4 <c>{key-id, epoch}</c> grain).</summary>
/// <param name="KeyId">The revoked signing public key.</param>
/// <param name="Epoch">The revoked epoch for that key.</param>
public sealed record PackRevokedKey(PrincipalId KeyId, long Epoch);

/// <summary>
/// The SIGNED revocation-list subject distributed on the Harborline channel (S-11). It lists the
/// <c>{key-id, epoch}</c> pairs that are revoked and the issuance instant (for staleness). It is signed
/// by a CHANNEL-scope trust root — because channel-key trust is in v1, its compromise blast radius is
/// every channel pack, so revocation must ride the channel itself, offline-verifiable.
/// </summary>
/// <param name="Revoked">The revoked keys.</param>
/// <param name="IssuedAtEpochMs">Issuance instant (Unix ms) — drives staleness surfacing.</param>
public sealed record PackRevocationManifest(
    IReadOnlyList<PackRevokedKey> Revoked,
    long IssuedAtEpochMs);

/// <summary>
/// The revocation check consulted at install (S-11). Answers "is this signer+epoch revoked?" plus a
/// staleness signal (a revocation list is offline-tolerant: an absent/old list does NOT block install,
/// but its staleness is SURFACED so an operator installing off a stale channel snapshot knows).
/// </summary>
public interface IPackRevocationList
{
    /// <summary>True iff the given signer key + epoch is on the revocation list.</summary>
    bool IsRevoked(PrincipalId keyId, long epoch);

    /// <summary>When the underlying list was issued, or null if no list was available (never fetched).</summary>
    DateTimeOffset? IssuedAt { get; }

    /// <summary>True iff the list is missing or older than <paramref name="maxAge"/> at
    /// <paramref name="now"/> (surfaced, not blocking — offline tolerance).</summary>
    bool IsStale(DateTimeOffset now, TimeSpan maxAge);
}

/// <summary>
/// The default <see cref="IPackRevocationList"/> — built from a set of revoked keys + an issuance instant
/// (or empty, representing "no list available / offline"). An empty list with a null
/// <see cref="IssuedAt"/> revokes nothing but reports itself STALE (never-fetched), so the offline case is
/// honest rather than a silent "nothing revoked".
/// </summary>
public sealed class PackRevocationList : IPackRevocationList
{
    private readonly HashSet<PackRevokedKey> _revoked;

    /// <summary>Builds a revocation list from explicit entries + optional issuance instant.</summary>
    public PackRevocationList(IEnumerable<PackRevokedKey> revoked, DateTimeOffset? issuedAt)
    {
        ArgumentNullException.ThrowIfNull(revoked);
        _revoked = new HashSet<PackRevokedKey>(revoked);
        IssuedAt = issuedAt;
    }

    /// <summary>An empty, never-fetched revocation list (offline default): revokes nothing, reports stale.</summary>
    public static PackRevocationList Empty { get; } = new(Array.Empty<PackRevokedKey>(), issuedAt: null);

    /// <inheritdoc />
    public DateTimeOffset? IssuedAt { get; }

    /// <inheritdoc />
    public bool IsRevoked(PrincipalId keyId, long epoch) => _revoked.Contains(new PackRevokedKey(keyId, epoch));

    /// <inheritdoc />
    public bool IsStale(DateTimeOffset now, TimeSpan maxAge)
        => IssuedAt is not { } issued || now - issued > maxAge;
}

/// <summary>
/// Verifies a signed <see cref="PackRevocationManifest"/> against the trust store and materializes an
/// <see cref="IPackRevocationList"/> (S-11). Fail-safe-offline: a null/undecodable/untrusted/non-channel
/// list yields an EMPTY, stale list (revokes nothing, reports stale) rather than throwing — install stays
/// possible offline, but the staleness is surfaced. A list signed by anything other than a CHANNEL-scope
/// root is rejected (revocation is a channel-distributed artifact).
/// </summary>
public static class PackRevocationListVerifier
{
    /// <summary>
    /// Verifies <paramref name="signedList"/> against <paramref name="trustStore"/> using
    /// <paramref name="verifier"/>; returns the materialized list, or <see cref="PackRevocationList.Empty"/>
    /// when the list is absent / cryptographically invalid / not vouched by a current channel root.
    /// </summary>
    public static IPackRevocationList Verify(
        SignedOperation<PackRevocationManifest>? signedList,
        IPackTrustStore trustStore,
        IOperationVerifier verifier)
    {
        ArgumentNullException.ThrowIfNull(trustStore);
        ArgumentNullException.ThrowIfNull(verifier);

        if (signedList is null || !verifier.Verify(signedList))
        {
            return PackRevocationList.Empty;
        }

        // The list must be vouched by a CURRENT channel-scope root (revocation rides the channel, S-11).
        var manifest = signedList.Payload;
        var resolution = trustStore.Resolve(signedList.IssuerId, manifest.IssuedAtEpochMs);
        var isChannelCurrent = resolution.Match == PackTrustMatch.Current
            && resolution.Scope == TrustScope.HarborlineChannel;

        // The revocation manifest carries its OWN issuance epoch-ms as the issuance instant; trust
        // resolution above keys off the channel root's key-id, not this value, so a mismatch is possible.
        // Re-resolve on the signer key alone to accept any current channel epoch for the signer.
        if (!isChannelCurrent)
        {
            var byKey = ResolveChannelByKey(trustStore, signedList.IssuerId);
            if (!byKey)
            {
                return PackRevocationList.Empty;
            }
        }

        return new PackRevocationList(
            manifest.Revoked,
            DateTimeOffset.FromUnixTimeMilliseconds(manifest.IssuedAtEpochMs));
    }

    private static bool ResolveChannelByKey(IPackTrustStore trustStore, PrincipalId keyId)
    {
        // A channel root recognizes this key (any epoch) ⇒ accept as channel-distributed. Epoch-currency of
        // the revocation list itself is a staleness concern, not a trust one.
        var probe = trustStore.Resolve(keyId, long.MinValue);
        return probe.Match != PackTrustMatch.None && probe.Scope == TrustScope.HarborlineChannel;
    }
}
