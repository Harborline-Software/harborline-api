namespace Harborline.Api.Kernel.Security.Crypto;

/// <summary>
/// The X-Wing sealed box — KEM suite #3 (<see cref="KemSuite.XWingX25519MlKem768_v1"/>, the PQC Phase 2 /
/// BL-01 <b>production</b> construction; CIC decision D1, ADR 0004 Amendment 2). Wraps the standardized
/// X-Wing hybrid KEM (<see cref="IXWingKem"/>) in the same AEAD-box pattern the suite-#1 and suite-#2
/// boxes use: encapsulate a 32-byte X-Wing shared secret to the recipient, stretch it through HKDF-SHA256
/// under a suite-#3-specific label, then ChaCha20-Poly1305.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a distinct suite from #2.</b> Suite #2 (<see cref="IHybridSealedBox"/>) is the TLS-concat
/// combiner kept as a tested reference. Suite #3 is the <i>named, IND-CCA-proven, KAT-vectored</i> X-Wing
/// construction — its combiner (<c>SHA3-256(ss_M ‖ ss_X ‖ ct_X ‖ pk_X ‖ XWingLabel)</c>) already binds the
/// X25519 ciphertext and the recipient's X25519 public key into the shared secret, so the box inherits
/// X-Wing's non-malleability. <b>Secure if EITHER half holds</b> — the combiner is a SHA3-256 PRF over both
/// the X25519 and the ML-KEM secret, so the AEAD key stays secret unless BOTH KEMs are broken.
/// </para>
/// <para>
/// <b>Domain separation.</b> The HKDF <c>info</c> label is suite-#3-specific
/// (<c>"sunfish-kernel-security:xwing-x25519-mlkem768-box:v1"</c>), distinct from suite #1's
/// (<c>"…:x25519-box:v1"</c>) and suite #2's (<c>"…:hybrid-x25519-mlkem768-box:v1"</c>), so no two suites
/// ever derive the same AEAD key — even from identical inputs. A suite-#1 or suite-#2 box is structurally
/// unopenable by the suite-#3 path and vice versa.
/// </para>
/// <para>
/// <b>Wire envelope.</b> The box carries the AEAD ciphertext+tag, the 24-byte nonce, and the 1120-byte
/// X-Wing KEM ciphertext (the recipient needs it to recover the X-Wing shared secret). Unlike suites #1/#2
/// there is no separate sender-ephemeral X25519 public key on the wire — X-Wing's ephemeral
/// <c>ct_X</c> is carried INSIDE the 1120-byte KEM ciphertext (<c>ct_M ‖ ct_X</c>).
/// </para>
/// <para>
/// <b>Fail-closed.</b> <see cref="OpenXWing"/> returns <c>null</c> on any AEAD authentication failure
/// (tampered ciphertext / nonce / KEM-ciphertext, wrong recipient key, ML-KEM implicit-rejection mismatch)
/// — it never throws on bad wire input, mirroring <see cref="IX25519KeyAgreement.OpenBox"/> and
/// <see cref="IHybridSealedBox.OpenHybrid"/>.
/// </para>
/// </remarks>
public interface IXWingSealedBox
{
    /// <summary>AEAD nonce length carried on the wire (24), matching suites #1 and #2.</summary>
    int NonceLength { get; }

    /// <summary>X-Wing KEM ciphertext length carried on the wire (1120).</summary>
    int KemCiphertextLength { get; }

    /// <summary>
    /// X-Wing private-key seed (decapsulation-key) length in bytes (32). A caller validates a supplied
    /// recipient seed against this BEFORE calling <see cref="OpenXWing"/> so a wrong-length seed fails closed
    /// (null) rather than throwing — the read-before-write capability gate (PQC Phase 2 increment 2c-iii).
    /// </summary>
    int PrivateKeySeedLength { get; }

    /// <summary>
    /// Box <paramref name="plaintext"/> for a recipient holding the 1216-byte X-Wing
    /// <paramref name="recipientXWingPublicKey"/>.
    /// </summary>
    /// <returns>
    /// The AEAD <c>Ciphertext</c> (plaintext length + 16-byte tag), the 24-byte <c>Nonce</c>, and the
    /// 1120-byte X-Wing <c>KemCiphertext</c> the recipient needs to recover the X-Wing shared secret.
    /// </returns>
    /// <exception cref="System.ArgumentException">The public key is the wrong length.</exception>
    (byte[] Ciphertext, byte[] Nonce, byte[] KemCiphertext) BoxXWing(
        ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> recipientXWingPublicKey);

    /// <summary>
    /// Open an X-Wing box with the recipient's 32-byte X-Wing private-key seed. Returns the plaintext, or
    /// <c>null</c> on ANY authentication failure (never throws on tampering). Throws only for a malformed
    /// seed / nonce length.
    /// </summary>
    byte[]? OpenXWing(
        ReadOnlySpan<byte> ciphertext,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> kemCiphertext,
        ReadOnlySpan<byte> recipientXWingPrivateKeySeed);
}
