using System.Security.Cryptography;

namespace Harborline.Api.Kernel.Security.Crypto;

/// <summary>
/// Default <see cref="IXWingSealedBox"/> — KEM suite #3 (<see cref="KemSuite.XWingX25519MlKem768_v1"/>).
/// Wraps the standardized X-Wing hybrid KEM (<see cref="IXWingKem"/>) in the suite-#1/#2 AEAD-box pattern:
/// X-Wing encapsulate → 32-byte shared secret → HKDF-SHA256 (suite-#3 label) → ChaCha20-Poly1305.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this defers the hybrid combiner to <see cref="IXWingKem"/>.</b> The X-Wing combiner
/// (<c>SHA3-256(ss_M ‖ ss_X ‖ ct_X ‖ pk_X ‖ XWingLabel)</c>) already produces the 32-byte hybrid secret
/// and binds the X25519 ciphertext + recipient public key — so this box does NOT re-derive or re-combine
/// the two KEM secrets the way <see cref="HybridSealedBox"/> does for suite #2. It treats X-Wing as a
/// black-box KEM and only adds the AEAD layer, with a suite-#3-specific HKDF label for domain separation.
/// </para>
/// <para>
/// <b>Nonce + AEAD layout</b> match suites #1/#2: a fresh 24-byte wire nonce, of which the first 12 bytes
/// salt HKDF and the last 12 bytes are the ChaCha20-Poly1305 nonce.
/// </para>
/// </remarks>
public sealed class XWingSealedBox : IXWingSealedBox
{
    private const int WireNonceLength = 24;
    private const int AeadKeyLength = 32;
    private const int AeadNonceLength = 12;
    private const int AuthTagLength = 16;

    // Suite-#3-specific HKDF "info" — distinct from suite #1's "…:x25519-box:v1" and suite #2's
    // "…:hybrid-x25519-mlkem768-box:v1", so no two suites ever derive the same AEAD key (domain separation
    // across the KEM-suite axis).
    private static readonly byte[] HkdfInfo =
        "sunfish-kernel-security:xwing-x25519-mlkem768-box:v1"u8.ToArray();

    private readonly IXWingKem _xwing;

    /// <summary>Construct over the X-Wing KEM primitive.</summary>
    public XWingSealedBox(IXWingKem xwing)
    {
        _xwing = xwing ?? throw new ArgumentNullException(nameof(xwing));
    }

    /// <inheritdoc />
    public int NonceLength => WireNonceLength;

    /// <inheritdoc />
    public int KemCiphertextLength => _xwing.CiphertextLength;

    /// <inheritdoc />
    public int PrivateKeySeedLength => _xwing.PrivateKeySeedLength;

    /// <inheritdoc />
    public (byte[] Ciphertext, byte[] Nonce, byte[] KemCiphertext) BoxXWing(
        ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> recipientXWingPublicKey)
    {
        if (recipientXWingPublicKey.Length != _xwing.PublicKeyLength)
        {
            throw new ArgumentException(
                $"X-Wing public key must be {_xwing.PublicKeyLength} bytes (was {recipientXWingPublicKey.Length}).",
                nameof(recipientXWingPublicKey));
        }

        // (1) X-Wing encapsulate → 32-byte shared secret + 1120-byte KEM ciphertext.
        var (kemCiphertext, sharedSecret) = _xwing.Encapsulate(recipientXWingPublicKey);

        var nonce = new byte[WireNonceLength];
        RandomNumberGenerator.Fill(nonce);

        byte[]? aeadKey = null;
        try
        {
            // (2) HKDF over the X-Wing shared secret, nonce-salted, suite-#3-labelled → AEAD key.
            aeadKey = DeriveAeadKey(sharedSecret, nonce.AsSpan(0, 12));

            var ciphertext = new byte[plaintext.Length + AuthTagLength];
            using (var chacha = new ChaCha20Poly1305(aeadKey))
            {
                chacha.Encrypt(
                    nonce: nonce.AsSpan(12, AeadNonceLength),
                    plaintext: plaintext,
                    ciphertext: ciphertext.AsSpan(0, plaintext.Length),
                    tag: ciphertext.AsSpan(plaintext.Length, AuthTagLength));
            }

            return (ciphertext, nonce, kemCiphertext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sharedSecret);
            if (aeadKey is not null) CryptographicOperations.ZeroMemory(aeadKey);
        }
    }

    /// <inheritdoc />
    public byte[]? OpenXWing(
        ReadOnlySpan<byte> ciphertext,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> kemCiphertext,
        ReadOnlySpan<byte> recipientXWingPrivateKeySeed)
    {
        if (recipientXWingPrivateKeySeed.Length != _xwing.PrivateKeySeedLength)
        {
            throw new ArgumentException(
                $"X-Wing private key seed must be {_xwing.PrivateKeySeedLength} bytes " +
                $"(was {recipientXWingPrivateKeySeed.Length}).",
                nameof(recipientXWingPrivateKeySeed));
        }
        if (nonce.Length != WireNonceLength)
        {
            throw new ArgumentException(
                $"Nonce must be {WireNonceLength} bytes (was {nonce.Length}).", nameof(nonce));
        }
        if (kemCiphertext.Length != _xwing.CiphertextLength || ciphertext.Length < AuthTagLength)
        {
            // Malformed KEM ciphertext or a ciphertext too short to hold a tag → not openable (fail closed).
            return null;
        }

        // X-Wing Decaps is fail-closed by construction: the ML-KEM half is IND-CCA2 (a tampered ct_M yields
        // a pseudo-random ss_M via implicit rejection, NOT an error), so a corrupt KEM ciphertext produces a
        // DIFFERENT X-Wing shared secret → an AEAD tag mismatch below → null. The ONE content-degenerate case
        // that the X25519 half rejects loudly is a low-order / all-zero ct_X (the contributory check) — that
        // is attacker-controllable wire data, so we catch it and fail closed (null) rather than throw.
        byte[]? sharedSecret = null;
        byte[]? aeadKey = null;
        try
        {
            try
            {
                sharedSecret = _xwing.Decapsulate(kemCiphertext, recipientXWingPrivateKeySeed);
            }
            catch (InvalidOperationException)
            {
                // X25519 contributory-check rejection on a degenerate ct_X (e.g. an all-zero / low-order
                // point in a malformed KEM ciphertext). Wire data → fail closed, never throw.
                return null;
            }
            aeadKey = DeriveAeadKey(sharedSecret, nonce[..12]);

            var plaintextLen = ciphertext.Length - AuthTagLength;
            var plaintext = new byte[plaintextLen];
            using var chacha = new ChaCha20Poly1305(aeadKey);
            try
            {
                chacha.Decrypt(
                    nonce: nonce.Slice(12, AeadNonceLength),
                    ciphertext: ciphertext[..plaintextLen],
                    tag: ciphertext.Slice(plaintextLen, AuthTagLength),
                    plaintext: plaintext);
            }
            catch (AuthenticationTagMismatchException)
            {
                CryptographicOperations.ZeroMemory(plaintext);
                return null;
            }

            return plaintext;
        }
        finally
        {
            if (sharedSecret is not null) CryptographicOperations.ZeroMemory(sharedSecret);
            if (aeadKey is not null) CryptographicOperations.ZeroMemory(aeadKey);
        }
    }

    /// <summary>
    /// HKDF-SHA256 over the X-Wing shared secret → 32-byte ChaCha20-Poly1305 key. The first 12 bytes of the
    /// wire nonce salt HKDF (per-message uniqueness); the suite-#3-specific <c>info</c> domain-separates
    /// suite #3 from suites #1 and #2.
    /// </summary>
    private static byte[] DeriveAeadKey(ReadOnlySpan<byte> sharedSecret, ReadOnlySpan<byte> nonceSalt)
    {
        var aeadKey = new byte[AeadKeyLength];
        HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            ikm: sharedSecret,
            output: aeadKey,
            salt: nonceSalt,
            info: HkdfInfo);
        return aeadKey;
    }
}
