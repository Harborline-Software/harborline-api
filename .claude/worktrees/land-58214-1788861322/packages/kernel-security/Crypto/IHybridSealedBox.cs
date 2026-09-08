namespace Harborline.Api.Kernel.Security.Crypto;

/// <summary>
/// The PQC <b>hybrid</b> sealed box — KEM suite #2 (<see cref="KemSuite.HybridX25519MlKem_v1"/>,
/// ADR 0004 §1). Combines the classical X25519 sealed box (<see cref="IX25519KeyAgreement"/>, the
/// suite-#1 construction, kept unchanged) with an ML-KEM-768 encapsulation (<see cref="IMlKem768"/>)
/// so the box is confidential if EITHER half holds.
/// </summary>
/// <remarks>
/// <para>
/// <b>Construction (the TLS-1.3 <c>X25519MLKEM768</c> hybrid pattern).</b> A recipient has TWO public
/// keys: an X25519 DM public key and an ML-KEM-768 encapsulation key. To box:
/// <list type="number">
///   <item>derive the X25519 shared secret exactly as suite #1 does (sender ephemeral X25519 priv ×
///     recipient X25519 pub);</item>
///   <item>ML-KEM-encapsulate a fresh secret to the recipient's ML-KEM public key (yields a KEM
///     ciphertext + the ML-KEM shared secret);</item>
///   <item><b>concatenate</b> the two shared secrets (X25519 ‖ ML-KEM) and run the pair through the
///     SAME HKDF-SHA256 → ChaCha20-Poly1305 AEAD the suite-#1 box uses, with a hybrid-specific HKDF
///     <c>info</c> label so the two suites never derive the same AEAD key from the same inputs.</item>
/// </list>
/// The wire envelope therefore carries the X25519 sender-ephemeral public key AND the ML-KEM ciphertext
/// (both public), plus the AEAD nonce and the ciphertext+tag.
/// </para>
/// <para>
/// <b>Why "secure if either half holds".</b> The AEAD key is HKDF(<c>X25519secret ‖ MLKEMsecret</c>).
/// To recover it an attacker must learn the HKDF input — i.e. BOTH shared secrets. A classical adversary
/// who can break X25519 (or a future CRQC that can break it via Shor) still cannot derive the ML-KEM
/// secret, so the HKDF input stays unknown; symmetrically, an adversary who could break ML-KEM still
/// cannot derive the X25519 secret. The box is broken only if BOTH KEMs are broken. Concatenation is a
/// secure KEM combiner here because HKDF-Extract over the joined secret is a PRF in each half
/// independently (the standard dual-PRF / TLS hybrid argument).
/// </para>
/// <para>
/// <b>Fail-closed.</b> <see cref="OpenHybrid"/> returns <c>null</c> on any AEAD authentication failure
/// (tampered ciphertext/nonce/KEM-ciphertext, wrong recipient key, ML-KEM implicit-rejection mismatch) —
/// it never throws on bad input, mirroring <see cref="IX25519KeyAgreement.OpenBox"/>.
/// </para>
/// </remarks>
public interface IHybridSealedBox
{
    /// <summary>AEAD nonce length carried on the wire (24), matching the suite-#1 box.</summary>
    int NonceLength { get; }

    /// <summary>
    /// Box <paramref name="plaintext"/> for a recipient holding <paramref name="recipientX25519PublicKey"/>
    /// (32 bytes) and <paramref name="recipientMlKemPublicKey"/> (1184 bytes), under a fresh
    /// <paramref name="senderEphemeralX25519PrivateKey"/> (32 bytes).
    /// </summary>
    /// <returns>
    /// The AEAD <c>Ciphertext</c> (plaintext length + 16-byte tag), the 24-byte <c>Nonce</c>, and the
    /// 1088-byte ML-KEM <c>KemCiphertext</c> the recipient needs to recover the ML-KEM secret. The sender
    /// ephemeral X25519 PUBLIC key is supplied by the caller alongside the private key and travels on the
    /// wire as it already does for suite #1.
    /// </returns>
    /// <exception cref="System.ArgumentException">Any key is the wrong length.</exception>
    (byte[] Ciphertext, byte[] Nonce, byte[] KemCiphertext) BoxHybrid(
        ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> recipientX25519PublicKey,
        ReadOnlySpan<byte> recipientMlKemPublicKey,
        ReadOnlySpan<byte> senderEphemeralX25519PrivateKey);

    /// <summary>
    /// Open a hybrid box. Returns the plaintext, or <c>null</c> on ANY authentication failure (never
    /// throws on tampering). Throws only for malformed key/nonce lengths.
    /// </summary>
    byte[]? OpenHybrid(
        ReadOnlySpan<byte> ciphertext,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> kemCiphertext,
        ReadOnlySpan<byte> senderEphemeralX25519PublicKey,
        ReadOnlySpan<byte> recipientX25519PrivateKey,
        ReadOnlySpan<byte> recipientMlKemPrivateKey);
}
