using Harborline.Api.Kernel.Security.Crypto;

namespace Harborline.Api.Foundation.Recovery;

/// <summary>
/// W#67 / ADR 0046-A6 — the trustee-side persisted record of a root-seed
/// envelope encrypted FOR a specific trustee BY the owner at trustee-
/// designation time. Set up via
/// <see cref="IRecoveryCoordinator.SetupTrusteeAsync"/>; consumed during
/// the recovery flow by <c>TrusteeSetupPage</c> + <c>ApproveRecoveryPage</c>
/// (W#67 PR 5) to derive the per-attestation re-encrypted envelope.
/// </summary>
/// <param name="TrusteeNodeId">The trustee's durable Harborline NodeId.</param>
/// <param name="OwnerEphX25519PublicKey">
/// The owner's ephemeral X25519 public key used as the sender during
/// the original <c>IX25519KeyAgreement.Box</c>. The trustee needs this
/// to <c>OpenBox</c> the envelope and recover the root seed.
/// </param>
/// <param name="Ciphertext">
/// The seed envelope ciphertext (32-byte seed + 16-byte auth tag = 48 B).
/// </param>
/// <param name="Nonce">
/// The 24-byte nonce returned by <see cref="Harborline.Api.Kernel.Security.Crypto.IX25519KeyAgreement.Box"/>.
/// </param>
public sealed record TrusteeEncryptedSeed(
    string TrusteeNodeId,
    byte[] OwnerEphX25519PublicKey,
    byte[] Ciphertext,
    byte[] Nonce)
{
    /// <summary>
    /// The sealed-box KEM suite that produced <see cref="Ciphertext"/> (ADR 0004 §1 KEM axis).
    /// Defaults to <see cref="KemSuites.LegacyDefault"/> (suite #1, the X25519 sealed box) so the
    /// 4-argument positional construction and every legacy untagged seed envelope resolve to the
    /// construction already in use. NOT part of the positional primary constructor — deliberately, to
    /// keep all existing call sites source-compatible. New code may set an explicit suite via a
    /// <c>with</c> expression once a second suite exists (Phase 2b).
    /// </summary>
    /// <remarks>
    /// <b>Layering.</b> <see cref="KemSuite"/> is kernel-local (it lives in <c>kernel-security</c>, the
    /// lower tier); this <c>foundation-recovery</c> record referencing it is a legal downward dependency.
    /// <b>Back-compat seam.</b> A seed envelope deserialized from a pre-this-change persisted form carries
    /// no suite tag; the reader leaves this at <see cref="KemSuites.LegacyDefault"/>. A future
    /// hybrid-aware open path MUST fail closed on an unregistered suite rather than silently treating an
    /// unknown tagged value as suite #1 — see <see cref="KemSuites.IsRegistered"/>.
    /// </remarks>
    public KemSuite Suite { get; init; } = KemSuites.LegacyDefault;
}
