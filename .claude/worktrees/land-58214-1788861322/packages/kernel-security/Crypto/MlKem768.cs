using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Kems;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace Harborline.Api.Kernel.Security.Crypto;

/// <summary>
/// Default <see cref="IMlKem768"/> backed by <c>BouncyCastle.Cryptography</c>'s FIPS-203 ML-KEM-768
/// implementation. Fixed to the <c>ml_kem_768</c> parameter set (NIST Category 3 — the PQC half of the
/// TLS-1.3 <c>X25519MLKEM768</c> hybrid).
/// </summary>
/// <remarks>
/// Stateless and thread-safe: every call constructs its own generator / encapsulator / decapsulator over
/// a fresh <see cref="SecureRandom"/>, mirroring how <see cref="X25519KeyAgreement"/> spins up per-call
/// NSec primitives. Keys are exchanged as the raw FIPS-203 encodings (<c>GetEncoded()</c> /
/// <c>FromEncoding</c>), so they round-trip across the wire and the test boundary without any
/// BouncyCastle types leaking past this class.
/// </remarks>
public sealed class MlKem768 : IMlKem768
{
    // FIPS-203 ML-KEM-768 fixed sizes (bytes). Asserted by the size-contract test.
    private const int EncapsulationKeyLength = 1184; // public / encapsulation key
    private const int DecapsulationKeyLength = 2400; // private / decapsulation key
    private const int EncapsCiphertextLength = 1088; // KEM ciphertext
    private const int SharedSecretBytes = 32;        // ML-KEM shared secret

    private static readonly MLKemParameters Params = MLKemParameters.ml_kem_768;

    /// <inheritdoc />
    public int PublicKeyLength => EncapsulationKeyLength;

    /// <inheritdoc />
    public int PrivateKeyLength => DecapsulationKeyLength;

    /// <inheritdoc />
    public int CiphertextLength => EncapsCiphertextLength;

    /// <inheritdoc />
    public int SharedSecretLength => SharedSecretBytes;

    /// <inheritdoc />
    public (byte[] PublicKey, byte[] PrivateKey) GenerateKeyPair()
    {
        var generator = new MLKemKeyPairGenerator();
        generator.Init(new MLKemKeyGenerationParameters(new SecureRandom(), Params));
        AsymmetricCipherKeyPair pair = generator.GenerateKeyPair();

        var publicKey = ((MLKemPublicKeyParameters)pair.Public).GetEncoded();
        var privateKey = ((MLKemPrivateKeyParameters)pair.Private).GetEncoded();
        return (publicKey, privateKey);
    }

    /// <inheritdoc />
    public (byte[] Ciphertext, byte[] SharedSecret) Encapsulate(ReadOnlySpan<byte> recipientPublicKey)
    {
        if (recipientPublicKey.Length != EncapsulationKeyLength)
        {
            throw new ArgumentException(
                $"ML-KEM-768 public key must be {EncapsulationKeyLength} bytes (was {recipientPublicKey.Length}).",
                nameof(recipientPublicKey));
        }

        var publicKey = MLKemPublicKeyParameters.FromEncoding(Params, recipientPublicKey.ToArray());

        var encapsulator = new MLKemEncapsulator(Params);
        encapsulator.Init(new ParametersWithRandom(publicKey, new SecureRandom()));

        var ciphertext = new byte[encapsulator.EncapsulationLength];
        var secret = new byte[encapsulator.SecretLength];
        encapsulator.Encapsulate(ciphertext, 0, ciphertext.Length, secret, 0, secret.Length);
        return (ciphertext, secret);
    }

    /// <inheritdoc />
    public byte[] Decapsulate(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> privateKey)
    {
        if (privateKey.Length != DecapsulationKeyLength)
        {
            throw new ArgumentException(
                $"ML-KEM-768 private key must be {DecapsulationKeyLength} bytes (was {privateKey.Length}).",
                nameof(privateKey));
        }
        if (ciphertext.Length != EncapsCiphertextLength)
        {
            throw new ArgumentException(
                $"ML-KEM-768 ciphertext must be {EncapsCiphertextLength} bytes (was {ciphertext.Length}).",
                nameof(ciphertext));
        }

        var decapsulationKey = MLKemPrivateKeyParameters.FromEncoding(Params, privateKey.ToArray());

        var decapsulator = new MLKemDecapsulator(Params);
        decapsulator.Init(decapsulationKey);

        var secret = new byte[decapsulator.SecretLength];
        decapsulator.Decapsulate(ciphertext.ToArray(), 0, ciphertext.Length, secret, 0, secret.Length);
        return secret;
    }
}
