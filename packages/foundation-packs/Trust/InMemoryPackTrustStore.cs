using Harborline.Api.Foundation.Crypto;

namespace Harborline.Api.Foundation.Packs.Trust;

/// <summary>
/// An in-memory <see cref="IPackTrustStore"/> built from a fixed set of scoped roots. Suitable for
/// the v1 two-root model (own-roster + Harborline-channel) and for tests. Resolution is default-deny
/// and honours the ADR 0126 D4 sealed-epoch distinction.
/// </summary>
public sealed class InMemoryPackTrustStore : IPackTrustStore
{
    private readonly IReadOnlyList<PackTrustRoot> _roots;

    /// <summary>Builds a trust store over <paramref name="roots"/> (defensively copied).</summary>
    public InMemoryPackTrustStore(IEnumerable<PackTrustRoot> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        _roots = roots.ToList();
    }

    /// <inheritdoc />
    public PackTrustResolution Resolve(PrincipalId keyId, long epoch)
    {
        // 1) An exact {key, epoch} match that is CURRENT — the only path to a full "Verified".
        foreach (var root in _roots)
        {
            if (root.KeyId.Equals(keyId) && root.Epoch == epoch && root.Status == TrustRootStatus.Current)
            {
                return new PackTrustResolution(PackTrustMatch.Current, root.Scope);
            }
        }

        // 2) An exact {key, epoch} match that is RETIRED (sealed prior epoch) — recognized, but the
        //    epoch is no longer current ⇒ epoch-unverifiable (S-11), never a tamper.
        foreach (var root in _roots)
        {
            if (root.KeyId.Equals(keyId) && root.Epoch == epoch && root.Status == TrustRootStatus.Retired)
            {
                return new PackTrustResolution(PackTrustMatch.RetiredOrUnverifiableEpoch, root.Scope);
            }
        }

        // 3) The KEY is recognized for this scope, but under a DIFFERENT epoch than the pack claims
        //    (a future/unknown epoch, or a sealed one we hold under a different number). The
        //    signature has already cryptographically bound this key+epoch, so this is not a tamper —
        //    it is "recognized signer, epoch we cannot currently affirm" ⇒ epoch-unverifiable (S-11).
        foreach (var root in _roots)
        {
            if (root.KeyId.Equals(keyId))
            {
                return new PackTrustResolution(PackTrustMatch.RetiredOrUnverifiableEpoch, root.Scope);
            }
        }

        // 4) No root recognizes this signer at all — fail-closed refuse (S-1).
        return PackTrustResolution.NotTrusted;
    }
}
