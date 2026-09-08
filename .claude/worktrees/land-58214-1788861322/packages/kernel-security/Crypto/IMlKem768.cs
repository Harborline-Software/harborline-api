namespace Harborline.Api.Kernel.Security.Crypto;

/// <summary>
/// ML-KEM-768 (FIPS-203, the NIST-standardized lattice KEM formerly "Kyber-768") — the
/// quantum-resistant half of the PQC hybrid sealed box (KEM suite #2, ADR 0004 §1). A KEM
/// (Key-Encapsulation Mechanism), not a sealed box on its own: the sender ENCAPSULATES a fresh
/// shared secret to the recipient's ML-KEM public key (producing an opaque ciphertext + the secret),
/// and the recipient DECAPSULATES the ciphertext with its private key to recover the same secret.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is NOT used standalone.</b> The recovered ML-KEM shared secret is concatenated with the
/// X25519 shared secret and the pair is run through the existing HKDF-SHA256 → ChaCha20-Poly1305 AEAD
/// (<see cref="IHybridSealedBox"/>). The hybrid is secure if EITHER the X25519 secret OR the ML-KEM
/// secret stays secret — a classical attacker must break X25519, a quantum attacker (CRQC) must break
/// ML-KEM, and breaking the AEAD requires breaking both halves of the HKDF input.
/// </para>
/// <para>
/// Backed by <c>BouncyCastle.Cryptography</c> (the CIC-selected provider; NSec has no ML-KEM yet).
/// Parameter set is fixed to <c>ml_kem_768</c> — NIST Category 3, the TLS-1.3 <c>X25519MLKEM768</c>
/// hybrid's PQC half. Encapsulation ciphertext is 1088 bytes; the public key is 1184 bytes; the private
/// (decapsulation) key is 2400 bytes; the shared secret is 32 bytes.
/// </para>
/// </remarks>
public interface IMlKem768
{
    /// <summary>ML-KEM-768 public (encapsulation) key length in bytes (1184).</summary>
    int PublicKeyLength { get; }

    /// <summary>ML-KEM-768 private (decapsulation) key length in bytes (2400).</summary>
    int PrivateKeyLength { get; }

    /// <summary>ML-KEM-768 encapsulation ciphertext length in bytes (1088).</summary>
    int CiphertextLength { get; }

    /// <summary>ML-KEM-768 shared-secret length in bytes (32).</summary>
    int SharedSecretLength { get; }

    /// <summary>Generates a fresh ML-KEM-768 key pair (raw FIPS-203 encodings).</summary>
    (byte[] PublicKey, byte[] PrivateKey) GenerateKeyPair();

    /// <summary>
    /// Sender side: encapsulate a fresh shared secret to <paramref name="recipientPublicKey"/>.
    /// Returns the opaque <c>Ciphertext</c> (sent to the recipient) and the <c>SharedSecret</c> (kept by
    /// the sender; the recipient recovers the identical secret via <see cref="Decapsulate"/>).
    /// </summary>
    /// <exception cref="System.ArgumentException">
    /// <paramref name="recipientPublicKey"/> is not <see cref="PublicKeyLength"/> bytes.
    /// </exception>
    (byte[] Ciphertext, byte[] SharedSecret) Encapsulate(ReadOnlySpan<byte> recipientPublicKey);

    /// <summary>
    /// Recipient side: decapsulate <paramref name="ciphertext"/> with <paramref name="privateKey"/> to
    /// recover the shared secret the sender encapsulated. ML-KEM is IND-CCA2: a malformed/tampered
    /// ciphertext yields a deterministic <i>pseudo-random</i> secret (implicit rejection) rather than an
    /// error, so a decapsulation mismatch surfaces as an AEAD auth-tag failure at the hybrid-box layer —
    /// fail-closed by construction.
    /// </summary>
    /// <exception cref="System.ArgumentException">
    /// <paramref name="privateKey"/> is not <see cref="PrivateKeyLength"/> bytes, or
    /// <paramref name="ciphertext"/> is not <see cref="CiphertextLength"/> bytes.
    /// </exception>
    byte[] Decapsulate(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> privateKey);
}
