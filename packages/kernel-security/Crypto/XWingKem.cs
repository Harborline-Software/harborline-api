using System.Reflection;
using System.Security.Cryptography;

using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Kems;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace Harborline.Api.Kernel.Security.Crypto;

/// <summary>
/// Default <see cref="IXWingKem"/> — the IETF/CFRG X-Wing hybrid KEM (<c>draft-connolly-cfrg-xwing-kem</c>),
/// assembled EXACTLY per the draft over <c>BouncyCastle.Cryptography</c>'s FIPS-203 ML-KEM-768, X25519, and
/// SHAKE256/SHA3-256 primitives. BouncyCastle 2.6.2 does not ship an X-Wing type, so the construction is
/// composed here; it is verified against the official X-Wing Known-Answer-Test vectors (see
/// <c>XWingKatTests</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Construction (verbatim per the draft).</b>
/// <list type="bullet">
///   <item><c>expandDecapsulationKey(seed)</c>: <c>expanded = SHAKE256(seed, 96)</c>;
///     <c>(pk_M, sk_M) = ML-KEM.KeyGen(expanded[0:64])</c>; <c>sk_X = expanded[64:96]</c>;
///     <c>pk_X = X25519(sk_X, base)</c>.</item>
///   <item><c>Encaps(pk)</c>: <c>ek_X = random(32)</c>; <c>ct_X = X25519(ek_X, base)</c>;
///     <c>ss_X = X25519(ek_X, pk_X)</c>; <c>(ss_M, ct_M) = ML-KEM.Encaps(pk_M)</c>;
///     <c>ss = Combiner(ss_M, ss_X, ct_X, pk_X)</c>; <c>ct = ct_M ‖ ct_X</c>.</item>
///   <item><c>Decaps(sk, ct)</c>: split <c>ct = ct_M ‖ ct_X</c>;
///     <c>ss_M = ML-KEM.Decaps(ct_M, sk_M)</c>; <c>ss_X = X25519(sk_X, ct_X)</c>;
///     <c>ss = Combiner(ss_M, ss_X, ct_X, pk_X)</c>.</item>
///   <item><c>Combiner(ss_M, ss_X, ct_X, pk_X) = SHA3-256(ss_M ‖ ss_X ‖ ct_X ‖ pk_X ‖ XWingLabel)</c>.</item>
/// </list>
/// </para>
/// <para>
/// Stateless and thread-safe: every call constructs its own BouncyCastle primitives over a fresh
/// <see cref="SecureRandom"/>, mirroring <see cref="MlKem768"/> and <see cref="X25519KeyAgreement"/>. Keys
/// and ciphertexts cross the boundary as raw byte encodings, so no BouncyCastle type leaks past this class.
/// </para>
/// </remarks>
public sealed class XWingKem : IXWingKem
{
    // X-Wing fixed sizes (bytes), per the draft.
    private const int SeedLength = 32;             // decapsulation key = a 32-byte seed
    private const int MlKemPublicKeyLength = 1184; // ML-KEM-768 encapsulation key
    private const int MlKemCiphertextLength = 1088; // ML-KEM-768 ciphertext
    private const int X25519KeyLength = 32;         // X25519 public key / ct_X
    private const int XWingPublicKeyLength = MlKemPublicKeyLength + X25519KeyLength;     // 1216
    private const int XWingCiphertextLength = MlKemCiphertextLength + X25519KeyLength;   // 1120
    private const int SharedSecretBytes = 32;       // SHA3-256 output

    private const int ShakeExpandBytes = 96;        // SHAKE256(seed, 96): (d‖z)[0:64] + sk_X[64:96]
    private const int MlKemKeyGenSeedLength = 64;   // (d ‖ z)

    // The X-Wing combiner domain separator: concat("\./", "/^\") = 6 bytes 5c 2e 2f 2f 5e 5c (the draft's
    // XWingLabel). Domain-separates the X-Wing shared secret from any other SHA3-256 use.
    private static readonly byte[] XWingLabel = { 0x5c, 0x2e, 0x2f, 0x2f, 0x5e, 0x5c };

    private static readonly MLKemParameters Params = MLKemParameters.ml_kem_768;

    /// <inheritdoc />
    public int PrivateKeySeedLength => SeedLength;

    /// <inheritdoc />
    public int PublicKeyLength => XWingPublicKeyLength;

    /// <inheritdoc />
    public int CiphertextLength => XWingCiphertextLength;

    /// <inheritdoc />
    public int SharedSecretLength => SharedSecretBytes;

    /// <inheritdoc />
    public (byte[] PublicKey, byte[] PrivateKeySeed) GenerateKeyPair()
    {
        var seed = new byte[SeedLength];
        RandomNumberGenerator.Fill(seed);
        var publicKey = DerivePublicKey(seed);
        return (publicKey, seed);
    }

    /// <summary>
    /// Deterministically derive the 1216-byte X-Wing public key for a given 32-byte seed — the SAME
    /// expansion <see cref="GenerateKeyPair"/> performs (it calls this with a random seed). Public on the
    /// <see cref="IXWingKem"/> contract (PQC Phase 2 increment 2c-iii) so a recipient whose X-Wing seed is
    /// HKDF-derived from the install root seed can re-derive its matching public key without persisting it;
    /// also lets the official X-Wing KAT vectors validate the PRODUCTION key-derivation path (not a
    /// duplicated test-local copy that could share a bug).
    /// </summary>
    public byte[] DerivePublicKey(ReadOnlySpan<byte> seed)
    {
        if (seed.Length != SeedLength)
        {
            throw new ArgumentException(
                $"X-Wing seed must be {SeedLength} bytes (was {seed.Length}).", nameof(seed));
        }

        var expanded = ExpandSeed(seed);
        try
        {
            // pk_M from the ML-KEM key-gen seed (d ‖ z), pk_X from the X25519 scalar.
            var (_, mlKemPublic, _, x25519Public) = DeriveKeyMaterial(expanded);
            return ConcatPublicKey(mlKemPublic, x25519Public);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expanded);
        }
    }

    /// <inheritdoc />
    public (byte[] Ciphertext, byte[] SharedSecret) Encapsulate(ReadOnlySpan<byte> recipientPublicKey)
    {
        if (recipientPublicKey.Length != XWingPublicKeyLength)
        {
            throw new ArgumentException(
                $"X-Wing public key must be {XWingPublicKeyLength} bytes (was {recipientPublicKey.Length}).",
                nameof(recipientPublicKey));
        }

        var pkM = recipientPublicKey[..MlKemPublicKeyLength].ToArray();
        var pkX = recipientPublicKey[MlKemPublicKeyLength..].ToArray();

        // (1) X25519 ephemeral: ct_X = X25519(ek_X, base); ss_X = X25519(ek_X, pk_X).
        var ekXseed = new byte[X25519KeyLength];
        RandomNumberGenerator.Fill(ekXseed);
        var ssX = new byte[X25519KeyLength];
        byte[] ctX;
        try
        {
            var ekX = new X25519PrivateKeyParameters(ekXseed, 0);
            ctX = ekX.GeneratePublicKey().GetEncoded();
            ekX.GenerateSecret(new X25519PublicKeyParameters(pkX, 0), ssX, 0);

            // (2) ML-KEM encapsulate (fresh randomness) → (ss_M, ct_M).
            var mlKemPublic = MLKemPublicKeyParameters.FromEncoding(Params, pkM);
            var encapsulator = new MLKemEncapsulator(Params);
            encapsulator.Init(new ParametersWithRandom(mlKemPublic, new SecureRandom()));
            var ctM = new byte[encapsulator.EncapsulationLength];
            var ssM = new byte[encapsulator.SecretLength];
            encapsulator.Encapsulate(ctM, 0, ctM.Length, ssM, 0, ssM.Length);
            try
            {
                // (3) Combiner + ciphertext concat.
                var ss = Combiner(ssM, ssX, ctX, pkX);
                var ct = ConcatCiphertext(ctM, ctX);
                return (ct, ss);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(ssM);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ekXseed);
            CryptographicOperations.ZeroMemory(ssX);
        }
    }

    /// <inheritdoc />
    public byte[] Decapsulate(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> privateKeySeed)
    {
        if (privateKeySeed.Length != SeedLength)
        {
            throw new ArgumentException(
                $"X-Wing private key seed must be {SeedLength} bytes (was {privateKeySeed.Length}).",
                nameof(privateKeySeed));
        }
        if (ciphertext.Length != XWingCiphertextLength)
        {
            throw new ArgumentException(
                $"X-Wing ciphertext must be {XWingCiphertextLength} bytes (was {ciphertext.Length}).",
                nameof(ciphertext));
        }

        var ctM = ciphertext[..MlKemCiphertextLength].ToArray();
        var ctX = ciphertext[MlKemCiphertextLength..].ToArray();

        var expanded = ExpandSeed(privateKeySeed);
        var ssX = new byte[X25519KeyLength];
        byte[]? ssM = null;
        try
        {
            var (mlKemPrivate, _, x25519Private, x25519Public) = DeriveKeyMaterial(expanded);

            // ss_X = X25519(sk_X, ct_X). ss_M = ML-KEM.Decaps(ct_M, sk_M).
            x25519Private.GenerateSecret(new X25519PublicKeyParameters(ctX, 0), ssX, 0);

            var decapsulator = new MLKemDecapsulator(Params);
            decapsulator.Init(mlKemPrivate);
            ssM = new byte[decapsulator.SecretLength];
            // FIPS-203 implicit rejection: a tampered ct_M yields a pseudo-random ss_M (never an error),
            // so a corrupt ciphertext flows through to a DIFFERENT combined secret here (fail-closed at
            // the box layer), exactly mirroring MlKem768.Decapsulate.
            decapsulator.Decapsulate(ctM, 0, ctM.Length, ssM, 0, ssM.Length);

            return Combiner(ssM, ssX, ctX, x25519Public.GetEncoded());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expanded);
            CryptographicOperations.ZeroMemory(ssX);
            if (ssM is not null) CryptographicOperations.ZeroMemory(ssM);
        }
    }

    // ── DERANDOMIZED ENCAPS (test-only, for the official X-Wing KAT vectors) ───────────────────────

    /// <summary>
    /// EncapsulateDerand(pk, eseed) per the draft — the deterministic encapsulation used ONLY to
    /// reproduce the official X-Wing Known-Answer-Test vectors. <paramref name="eseed"/> is 64 bytes:
    /// <c>eseed[0:32]</c> are the ML-KEM encapsulation coins <c>m</c> and <c>eseed[32:64]</c> is the
    /// X25519 ephemeral scalar <c>ek_X</c>. NOT a production path (production
    /// <see cref="Encapsulate"/> draws fresh randomness) — exposed to the test assembly only.
    /// </summary>
    internal (byte[] Ciphertext, byte[] SharedSecret) EncapsulateDerand(
        ReadOnlySpan<byte> recipientPublicKey, ReadOnlySpan<byte> eseed)
    {
        if (recipientPublicKey.Length != XWingPublicKeyLength)
        {
            throw new ArgumentException(
                $"X-Wing public key must be {XWingPublicKeyLength} bytes (was {recipientPublicKey.Length}).",
                nameof(recipientPublicKey));
        }
        if (eseed.Length != 64)
        {
            throw new ArgumentException($"X-Wing eseed must be 64 bytes (was {eseed.Length}).", nameof(eseed));
        }

        var pkM = recipientPublicKey[..MlKemPublicKeyLength].ToArray();
        var pkX = recipientPublicKey[MlKemPublicKeyLength..].ToArray();
        var mlKemCoins = eseed[..32].ToArray();
        var ekXseed = eseed[32..].ToArray();

        var ssX = new byte[X25519KeyLength];
        try
        {
            var ekX = new X25519PrivateKeyParameters(ekXseed, 0);
            var ctX = ekX.GeneratePublicKey().GetEncoded();
            ekX.GenerateSecret(new X25519PublicKeyParameters(pkX, 0), ssX, 0);

            var (ssM, ctM) = MlKemEncapsDerand(pkM, mlKemCoins);
            try
            {
                var ss = Combiner(ssM, ssX, ctX, pkX);
                var ct = ConcatCiphertext(ctM, ctX);
                return (ct, ss);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(ssM);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ekXseed);
            CryptographicOperations.ZeroMemory(ssX);
        }
    }

    // BouncyCastle 2.6.2 has no public ML-KEM EncapsDerand; the internal MLKemPublicKeyParameters
    // .InternalEncapsulate(m) returns (ct_M, ss_M) for a 32-byte message m. Reached via reflection and
    // used ONLY by EncapsulateDerand (the KAT path), never by the production Encapsulate.
    private static readonly MethodInfo InternalEncapsulateMethod =
        typeof(MLKemPublicKeyParameters).GetMethod(
            "InternalEncapsulate", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException(
            "BouncyCastle MLKemPublicKeyParameters.InternalEncapsulate(byte[]) not found — the X-Wing KAT " +
            "derand path requires it. This is a test-only path; production Encapsulate does not use it.");

    private static (byte[] SsM, byte[] CtM) MlKemEncapsDerand(byte[] pkM, byte[] coins)
    {
        var mlKemPublic = MLKemPublicKeyParameters.FromEncoding(Params, pkM);
        var tuple = InternalEncapsulateMethod.Invoke(mlKemPublic, new object[] { coins })
            ?? throw new InvalidOperationException("ML-KEM InternalEncapsulate returned null.");
        var item1 = (byte[])tuple.GetType().GetProperty("Item1")!.GetValue(tuple)!;
        var item2 = (byte[])tuple.GetType().GetProperty("Item2")!.GetValue(tuple)!;
        // Item1 = ct_M (1088), Item2 = ss_M (32) — length-disambiguated so the order is never assumed.
        var ssM = item1.Length == SharedSecretBytes ? item1 : item2;
        var ctM = item1.Length == MlKemCiphertextLength ? item1 : item2;
        return (ssM, ctM);
    }

    // ── shared helpers ─────────────────────────────────────────────────────────────────────────────

    private static byte[] ExpandSeed(ReadOnlySpan<byte> seed)
    {
        var shake = new ShakeDigest(256);
        shake.BlockUpdate(seed.ToArray(), 0, seed.Length);
        var expanded = new byte[ShakeExpandBytes];
        shake.OutputFinal(expanded, 0, expanded.Length);
        return expanded;
    }

    /// <summary>
    /// Derive the four key components from the 96-byte SHAKE256 expansion: ML-KEM (sk_M, pk_M) from
    /// <c>expanded[0:64]</c> via the FIPS-203 deterministic key-gen seed (d ‖ z), and X25519 (sk_X, pk_X)
    /// from <c>expanded[64:96]</c>.
    /// </summary>
    private static (MLKemPrivateKeyParameters MlKemPrivate, byte[] MlKemPublic,
        X25519PrivateKeyParameters X25519Private, X25519PublicKeyParameters X25519Public)
        DeriveKeyMaterial(byte[] expanded)
    {
        var keyGenSeed = expanded[..MlKemKeyGenSeedLength];
        try
        {
            var mlKemPrivate = MLKemPrivateKeyParameters.FromSeed(Params, keyGenSeed);
            var mlKemPublic = ((MLKemPublicKeyParameters)mlKemPrivate.GetPublicKey()).GetEncoded();

            var x25519Private = new X25519PrivateKeyParameters(expanded, MlKemKeyGenSeedLength);
            var x25519Public = x25519Private.GeneratePublicKey();
            return (mlKemPrivate, mlKemPublic, x25519Private, x25519Public);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyGenSeed);
        }
    }

    private static byte[] Combiner(
        ReadOnlySpan<byte> ssM, ReadOnlySpan<byte> ssX, ReadOnlySpan<byte> ctX, ReadOnlySpan<byte> pkX)
    {
        // ss = SHA3-256( ss_M ‖ ss_X ‖ ct_X ‖ pk_X ‖ XWingLabel ).
        var sha3 = new Sha3Digest(256);
        sha3.BlockUpdate(ssM.ToArray(), 0, ssM.Length);
        sha3.BlockUpdate(ssX.ToArray(), 0, ssX.Length);
        sha3.BlockUpdate(ctX.ToArray(), 0, ctX.Length);
        sha3.BlockUpdate(pkX.ToArray(), 0, pkX.Length);
        sha3.BlockUpdate(XWingLabel, 0, XWingLabel.Length);
        var ss = new byte[SharedSecretBytes];
        sha3.DoFinal(ss, 0);
        return ss;
    }

    private static byte[] ConcatPublicKey(byte[] mlKemPublic, X25519PublicKeyParameters x25519Public)
    {
        var publicKey = new byte[XWingPublicKeyLength];
        mlKemPublic.CopyTo(publicKey, 0);
        x25519Public.GetEncoded().CopyTo(publicKey, MlKemPublicKeyLength);
        return publicKey;
    }

    private static byte[] ConcatCiphertext(byte[] ctM, byte[] ctX)
    {
        var ct = new byte[XWingCiphertextLength];
        ctM.CopyTo(ct, 0);
        ctX.CopyTo(ct, MlKemCiphertextLength);
        return ct;
    }
}
