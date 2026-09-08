using System.Security.Cryptography;
using NSec.Cryptography;

namespace Harborline.Api.Kernel.Security.Crypto;

/// <summary>
/// Default <see cref="IHybridSealedBox"/> — KEM suite #2 (<see cref="KemSuite.HybridX25519MlKem_v1"/>).
/// Composes the classical X25519 ECDH shared secret (via <c>NSec.Cryptography</c>, the SAME primitive and
/// derivation suite #1 uses) with an ML-KEM-768 encapsulated secret (via <see cref="IMlKem768"/>), then
/// runs the CONCATENATED pair through HKDF-SHA256 → ChaCha20-Poly1305 — secure if EITHER half holds.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why suite #1 is untouched.</b> This class does its own X25519 <c>Agree</c> + HKDF rather than
/// calling <see cref="X25519KeyAgreement"/>, because the hybrid HKDF input is <c>X25519 ‖ ML-KEM</c>
/// (not the X25519 secret alone) and the HKDF <c>info</c> label is hybrid-specific. Suite #1's
/// <see cref="X25519KeyAgreement"/> derivation — and every box it has ever produced — is therefore
/// byte-for-byte unchanged; suite #2 is a strictly additive new construction.
/// </para>
/// <para>
/// <b>Nonce + AEAD layout</b> match suite #1: a fresh 24-byte wire nonce, of which the first 12 bytes
/// salt HKDF and the last 12 bytes are the ChaCha20-Poly1305 nonce.
/// </para>
/// </remarks>
public sealed class HybridSealedBox : IHybridSealedBox
{
    private const int X25519KeyLength = 32;
    private const int WireNonceLength = 24;
    private const int AeadKeyLength = 32;
    private const int AeadNonceLength = 12;
    private const int AuthTagLength = 16;

    private static readonly KeyAgreementAlgorithm Kem = KeyAgreementAlgorithm.X25519;

    // Hybrid-specific HKDF "info" — distinct from suite #1's "sunfish-kernel-security:x25519-box:v1"
    // so the two suites NEVER derive the same AEAD key even from identical X25519 inputs (domain
    // separation across the KEM-suite axis).
    private static readonly byte[] HkdfInfo = "sunfish-kernel-security:hybrid-x25519-mlkem768-box:v1"u8.ToArray();

    private readonly IMlKem768 _mlKem;

    /// <summary>Construct over the ML-KEM-768 primitive (X25519 is done in-class via NSec).</summary>
    public HybridSealedBox(IMlKem768 mlKem)
    {
        _mlKem = mlKem ?? throw new ArgumentNullException(nameof(mlKem));
    }

    /// <inheritdoc />
    public int NonceLength => WireNonceLength;

    /// <inheritdoc />
    public (byte[] Ciphertext, byte[] Nonce, byte[] KemCiphertext) BoxHybrid(
        ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> recipientX25519PublicKey,
        ReadOnlySpan<byte> recipientMlKemPublicKey,
        ReadOnlySpan<byte> senderEphemeralX25519PrivateKey)
    {
        if (senderEphemeralX25519PrivateKey.Length != X25519KeyLength)
        {
            throw new ArgumentException(
                $"X25519 private key must be {X25519KeyLength} bytes (was {senderEphemeralX25519PrivateKey.Length}).",
                nameof(senderEphemeralX25519PrivateKey));
        }
        if (recipientX25519PublicKey.Length != X25519KeyLength)
        {
            throw new ArgumentException(
                $"X25519 public key must be {X25519KeyLength} bytes (was {recipientX25519PublicKey.Length}).",
                nameof(recipientX25519PublicKey));
        }
        if (recipientMlKemPublicKey.Length != _mlKem.PublicKeyLength)
        {
            throw new ArgumentException(
                $"ML-KEM public key must be {_mlKem.PublicKeyLength} bytes (was {recipientMlKemPublicKey.Length}).",
                nameof(recipientMlKemPublicKey));
        }

        // (1) ML-KEM encapsulate a fresh secret to the recipient's PQC public key.
        var (kemCiphertext, mlKemSecret) = _mlKem.Encapsulate(recipientMlKemPublicKey);

        var nonce = new byte[WireNonceLength];
        RandomNumberGenerator.Fill(nonce);

        byte[]? x25519Secret = null;
        byte[]? aeadKey = null;
        try
        {
            // (2) Classical X25519 ECDH secret (sender ephemeral priv × recipient pub).
            x25519Secret = DeriveX25519Secret(senderEphemeralX25519PrivateKey, recipientX25519PublicKey);

            // (3) HKDF over (X25519 ‖ ML-KEM), nonce-salted, hybrid-labelled → AEAD key.
            aeadKey = DeriveHybridAeadKey(x25519Secret, mlKemSecret, nonce.AsSpan(0, 12));

            var ciphertext = new byte[plaintext.Length + AuthTagLength];
            using (var chacha = new System.Security.Cryptography.ChaCha20Poly1305(aeadKey))
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
            if (x25519Secret is not null) CryptographicOperations.ZeroMemory(x25519Secret);
            if (aeadKey is not null) CryptographicOperations.ZeroMemory(aeadKey);
            CryptographicOperations.ZeroMemory(mlKemSecret);
        }
    }

    /// <inheritdoc />
    public byte[]? OpenHybrid(
        ReadOnlySpan<byte> ciphertext,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> kemCiphertext,
        ReadOnlySpan<byte> senderEphemeralX25519PublicKey,
        ReadOnlySpan<byte> recipientX25519PrivateKey,
        ReadOnlySpan<byte> recipientMlKemPrivateKey)
    {
        if (recipientX25519PrivateKey.Length != X25519KeyLength)
        {
            throw new ArgumentException(
                $"X25519 private key must be {X25519KeyLength} bytes (was {recipientX25519PrivateKey.Length}).",
                nameof(recipientX25519PrivateKey));
        }
        if (senderEphemeralX25519PublicKey.Length != X25519KeyLength)
        {
            throw new ArgumentException(
                $"X25519 public key must be {X25519KeyLength} bytes (was {senderEphemeralX25519PublicKey.Length}).",
                nameof(senderEphemeralX25519PublicKey));
        }
        if (nonce.Length != WireNonceLength)
        {
            throw new ArgumentException(
                $"Nonce must be {WireNonceLength} bytes (was {nonce.Length}).", nameof(nonce));
        }
        if (recipientMlKemPrivateKey.Length != _mlKem.PrivateKeyLength)
        {
            throw new ArgumentException(
                $"ML-KEM private key must be {_mlKem.PrivateKeyLength} bytes (was {recipientMlKemPrivateKey.Length}).",
                nameof(recipientMlKemPrivateKey));
        }
        if (kemCiphertext.Length != _mlKem.CiphertextLength || ciphertext.Length < AuthTagLength)
        {
            // Malformed KEM ciphertext or a ciphertext too short to hold a tag → not openable.
            return null;
        }

        // ML-KEM Decapsulate is IND-CCA2: a tampered KEM ciphertext yields a deterministic PSEUDO-random
        // secret (implicit rejection), NOT an error — so a corrupt KEM ciphertext surfaces as an AEAD
        // tag mismatch below (fail-closed), exactly like a corrupt X25519 path.
        byte[]? mlKemSecret = null;
        byte[]? x25519Secret = null;
        byte[]? aeadKey = null;
        try
        {
            mlKemSecret = _mlKem.Decapsulate(kemCiphertext, recipientMlKemPrivateKey);
            x25519Secret = DeriveX25519Secret(recipientX25519PrivateKey, senderEphemeralX25519PublicKey);
            aeadKey = DeriveHybridAeadKey(x25519Secret, mlKemSecret, nonce[..12]);

            var plaintextLen = ciphertext.Length - AuthTagLength;
            var plaintext = new byte[plaintextLen];
            using var chacha = new System.Security.Cryptography.ChaCha20Poly1305(aeadKey);
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
            if (mlKemSecret is not null) CryptographicOperations.ZeroMemory(mlKemSecret);
            if (x25519Secret is not null) CryptographicOperations.ZeroMemory(x25519Secret);
            if (aeadKey is not null) CryptographicOperations.ZeroMemory(aeadKey);
        }
    }

    /// <summary>
    /// Raw X25519(<paramref name="privateKey"/>, <paramref name="peerPublicKey"/>) shared secret (32 bytes),
    /// the SAME ECDH the suite-#1 box uses. The HKDF stretching happens in
    /// <see cref="DeriveHybridAeadKey"/> over the concatenated secret, not here.
    /// </summary>
    private static byte[] DeriveX25519Secret(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> peerPublicKey)
    {
        using var myKey = Key.Import(Kem, privateKey, KeyBlobFormat.RawPrivateKey);
        var peer = PublicKey.Import(Kem, peerPublicKey, KeyBlobFormat.RawPublicKey);

        var sharedParams = new SharedSecretCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport,
        };
        using var shared = Kem.Agree(myKey, peer, in sharedParams)
            ?? throw new CryptographicException(
                "X25519 key agreement failed (contributory check rejected the peer key).");

        return shared.Export(SharedSecretBlobFormat.RawSharedSecret);
    }

    /// <summary>
    /// HKDF-SHA256 over the CONCATENATED hybrid secret (<c>X25519 ‖ ML-KEM</c>) → 32-byte ChaCha20-Poly1305
    /// key. This is the secure KEM combiner: the AEAD key depends on BOTH shared secrets, so the box is
    /// confidential unless an attacker recovers both. The first 12 bytes of the wire nonce salt HKDF (per-
    /// message uniqueness); the hybrid-specific <c>info</c> domain-separates suite #2 from suite #1.
    /// </summary>
    private static byte[] DeriveHybridAeadKey(
        ReadOnlySpan<byte> x25519Secret,
        ReadOnlySpan<byte> mlKemSecret,
        ReadOnlySpan<byte> nonceSalt)
    {
        var combined = new byte[x25519Secret.Length + mlKemSecret.Length];
        try
        {
            x25519Secret.CopyTo(combined);
            mlKemSecret.CopyTo(combined.AsSpan(x25519Secret.Length));

            var aeadKey = new byte[AeadKeyLength];
            HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                ikm: combined,
                output: aeadKey,
                salt: nonceSalt,
                info: HkdfInfo);
            return aeadKey;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(combined);
        }
    }
}
