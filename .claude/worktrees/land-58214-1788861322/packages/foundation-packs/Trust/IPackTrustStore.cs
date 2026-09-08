using Harborline.Api.Foundation.Crypto;

namespace Harborline.Api.Foundation.Packs.Trust;

/// <summary>
/// The trust store — a DEFAULT-DENY resolver that answers exactly one question: "which scoped root
/// vouches for this <c>{key-id, epoch}</c>, and is that epoch current?" (design fold S-13).
/// </summary>
/// <remarks>
/// <para>
/// This interface is deliberately MINIMAL and has NO "install anyway" / "trust this byte" / bypass
/// method — enabling untrusted installs later must be a new ADR-gated feature, not a config flip on
/// a flat allow-list (S-13). The only decision surface is <see cref="Resolve"/>; a caller cannot ask
/// the store to trust something it does not recognize.
/// </para>
/// <para>
/// The store never verifies signatures itself — cryptographic verification is the verifier's job.
/// The store only maps a recognized signer + epoch to a scope + currency, so the verifier can
/// distinguish "untrusted" (refuse) from "recognized but retired epoch" (epoch-unverifiable, S-11).
/// </para>
/// </remarks>
public interface IPackTrustStore
{
    /// <summary>
    /// Resolves a signer public key + claimed epoch to a trust scope + currency. Returns
    /// <see cref="PackTrustResolution.NotTrusted"/> when no root recognizes the key.
    /// </summary>
    PackTrustResolution Resolve(PrincipalId keyId, long epoch);
}
