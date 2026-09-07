namespace Harborline.Api.Kernel.Security.Crypto;

/// <summary>
/// X-Wing (<c>draft-connolly-cfrg-xwing-kem</c>) — the IETF/CFRG general-purpose hybrid KEM that
/// combines X25519 with ML-KEM-768 (FIPS-203) into a SINGLE named, IND-CCA-proven, KAT-vectored
/// construction. The PQC Phase 2 / BL-01 <b>production</b> KEM (suite #3,
/// <see cref="KemSuite.XWingX25519MlKem768_v1"/>; CIC decision D1, ADR 0004 Amendment 2).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why X-Wing rather than the suite-#2 TLS-concat combiner.</b> Suite #2
/// (<see cref="IHybridSealedBox"/>) concatenates the two shared secrets and feeds the pair through
/// HKDF — a sound combiner, kept as a tested reference. X-Wing is the <i>standardized</i> hybrid: it
/// has a published IND-CCA security proof, official Known-Answer-Test vectors, and crucially its
/// combiner <b>binds the X25519 ciphertext and the recipient's X25519 public key</b> into the final
/// secret, giving the construction non-malleability without a surrounding transcript. The CIC picked
/// the named/proven/test-vectored construction over a bespoke combiner for the production cutover.
/// </para>
/// <para>
/// <b>The X-Wing construction (per the draft).</b> A recipient's private key is a 32-byte
/// <i>seed</i>; the public key is <c>pk_M ‖ pk_X</c> (the 1184-byte ML-KEM-768 encapsulation key
/// concatenated with the 32-byte X25519 public key = 1216 bytes). The seed expands via
/// <c>SHAKE256(seed, 96)</c> into the ML-KEM <c>(d ‖ z)</c> key-gen seed (bytes 0..64) and the X25519
/// scalar (bytes 64..96), so a key pair is fully determined by its 32-byte seed.
/// </para>
/// <list type="bullet">
///   <item><b>Encaps(pk):</b> draw a fresh 32-byte ML-KEM message <c>m</c> and a fresh 32-byte X25519
///     ephemeral scalar <c>ek_X</c>; compute <c>ct_X = X25519(ek_X, base)</c> and
///     <c>ss_X = X25519(ek_X, pk_X)</c>; compute <c>(ss_M, ct_M) = ML-KEM.Encaps(pk_M; m)</c>; the
///     shared secret is <c>ss = SHA3-256(ss_M ‖ ss_X ‖ ct_X ‖ pk_X ‖ XWingLabel)</c> and the
///     ciphertext is <c>ct = ct_M ‖ ct_X</c> (1088 + 32 = 1120 bytes).</item>
///   <item><b>Decaps(sk, ct):</b> expand the seed, split <c>ct</c> into <c>ct_M (1088) ‖ ct_X (32)</c>,
///     recover <c>ss_M = ML-KEM.Decaps(ct_M, sk_M)</c> and <c>ss_X = X25519(sk_X, ct_X)</c>, and apply
///     the IDENTICAL combiner.</item>
/// </list>
/// <para>
/// <b>The X-Wing combiner label</b> is the 6-byte ASCII string <c>concat("\\./", "/^\\")</c> =
/// <c>5c 2e 2f 2f 5e 5c</c> (the draft's <c>XWingLabel</c>). It domain-separates the X-Wing shared
/// secret from any other SHA3-256 use.
/// </para>
/// <para>
/// <b>Not used standalone for a box.</b> Like ML-KEM-768, the 32-byte X-Wing shared secret is the KEM
/// output; the AEAD sealed box (<see cref="IXWingSealedBox"/>) feeds it through HKDF-SHA256 →
/// ChaCha20-Poly1305 under a suite-#3-specific label. X-Wing is <b>secure if EITHER half holds</b> —
/// its combiner is a SHA3-256 PRF over both secrets, so the output stays unknown unless an attacker
/// recovers BOTH the X25519 and the ML-KEM secret.
/// </para>
/// <para>
/// Backed by <c>BouncyCastle.Cryptography</c> (2.6.2 does not ship X-Wing natively — the construction
/// is assembled here exactly per the draft over BouncyCastle's FIPS-203 ML-KEM-768 + X25519 +
/// SHAKE256/SHA3-256 primitives, verified against the official X-Wing KAT vectors).
/// </para>
/// </remarks>
public interface IXWingKem
{
    /// <summary>X-Wing decapsulation-key (private) length in bytes (32 — a seed).</summary>
    int PrivateKeySeedLength { get; }

    /// <summary>X-Wing encapsulation-key (public) length in bytes (1216 = ML-KEM-768 pk 1184 ‖ X25519 pk 32).</summary>
    int PublicKeyLength { get; }

    /// <summary>X-Wing ciphertext length in bytes (1120 = ML-KEM-768 ct 1088 ‖ X25519 ct 32).</summary>
    int CiphertextLength { get; }

    /// <summary>X-Wing shared-secret length in bytes (32).</summary>
    int SharedSecretLength { get; }

    /// <summary>
    /// Generates a fresh X-Wing key pair. The private key is a uniformly-random 32-byte
    /// <c>PrivateKeySeed</c>; the 1216-byte <c>PublicKey</c> is <c>pk_M ‖ pk_X</c> deterministically
    /// expanded from that seed.
    /// </summary>
    (byte[] PublicKey, byte[] PrivateKeySeed) GenerateKeyPair();

    /// <summary>
    /// Sender side: encapsulate a fresh shared secret to <paramref name="recipientPublicKey"/> (1216
    /// bytes). Returns the opaque 1120-byte <c>Ciphertext</c> (sent to the recipient) and the 32-byte
    /// X-Wing <c>SharedSecret</c> (kept by the sender; the recipient recovers the identical secret via
    /// <see cref="Decapsulate"/>). Draws fresh randomness — two calls to the same key yield distinct
    /// ciphertexts and secrets.
    /// </summary>
    /// <exception cref="System.ArgumentException">
    /// <paramref name="recipientPublicKey"/> is not <see cref="PublicKeyLength"/> bytes.
    /// </exception>
    (byte[] Ciphertext, byte[] SharedSecret) Encapsulate(ReadOnlySpan<byte> recipientPublicKey);

    /// <summary>
    /// Recipient side: decapsulate <paramref name="ciphertext"/> (1120 bytes) with the 32-byte
    /// <paramref name="privateKeySeed"/> to recover the shared secret the sender encapsulated. The
    /// ML-KEM half is IND-CCA2 (implicit rejection — a tampered <c>ct_M</c> yields a deterministic
    /// pseudo-random <c>ss_M</c> rather than an error), so a tampered ciphertext surfaces as a
    /// DIFFERENT X-Wing shared secret here, hence an AEAD auth-tag failure at the box layer
    /// (fail-closed by construction).
    /// </summary>
    /// <exception cref="System.ArgumentException">
    /// <paramref name="privateKeySeed"/> is not <see cref="PrivateKeySeedLength"/> bytes, or
    /// <paramref name="ciphertext"/> is not <see cref="CiphertextLength"/> bytes.
    /// </exception>
    byte[] Decapsulate(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> privateKeySeed);

    /// <summary>
    /// Deterministically derive the <see cref="PublicKeyLength"/>-byte X-Wing public key
    /// (<c>pk_M ‖ pk_X</c>) for a given 32-byte <paramref name="privateKeySeed"/> — the SAME expansion
    /// <see cref="GenerateKeyPair"/> performs internally (it draws a random seed and calls this). Exposed
    /// so a recipient whose X-Wing private-key seed is HKDF-derived from the install root seed (PQC Phase 2
    /// increment 2c-iii — the recipient X-Wing key model) can publish / re-derive its matching public key
    /// without persisting it, exactly as the X25519 DM key model re-derives its public half each boot.
    /// </summary>
    /// <exception cref="System.ArgumentException">
    /// <paramref name="privateKeySeed"/> is not <see cref="PrivateKeySeedLength"/> bytes.
    /// </exception>
    byte[] DerivePublicKey(ReadOnlySpan<byte> privateKeySeed);
}
