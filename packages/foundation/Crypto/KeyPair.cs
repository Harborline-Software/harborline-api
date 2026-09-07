using NSec.Cryptography;

namespace Harborline.Api.Foundation.Crypto;

/// <summary>
/// An Ed25519 keypair wrapping an <see cref="NSec.Cryptography.Key"/>. Generated keypairs must be
/// disposed to release unmanaged secret-key material (NSec zeroes it on dispose).
/// </summary>
public sealed class KeyPair : IDisposable
{
    private readonly Key _key;
    private readonly PrincipalId _principalId;

    private KeyPair(Key key)
    {
        _key = key;

        // Export the public key as raw 32 bytes and wrap it as PrincipalId.
        var publicBlob = _key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        _principalId = PrincipalId.FromBytes(publicBlob);
    }

    /// <summary>Generates a fresh Ed25519 keypair.</summary>
    /// <remarks>The underlying key is created with <see cref="KeyExportPolicies.AllowPlaintextExport"/>
    /// so the public-key material can be re-exported. The secret key is not exported by this class.</remarks>
    public static KeyPair Generate()
    {
        var creationParameters = new KeyCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport,
        };
        var key = Key.Create(SignatureAlgorithm.Ed25519, creationParameters);
        return new KeyPair(key);
    }

    /// <summary>
    /// Reconstructs a keypair from a raw 32-byte Ed25519 SEED (the raw private key).
    /// The same seed always yields the same <see cref="PrincipalId"/> + signatures, so this is
    /// the deterministic bridge from a host-provisioned identity (e.g. the local-node-host's
    /// <c>LocalNodeOptions.RootSeedHex</c> → <c>NodeIdentity</c>) into the foundation
    /// <see cref="CanonicalJson"/>/<see cref="SignedOperation{T}"/> signing path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The seed is the raw 32-byte Ed25519 private scalar — the SAME byte form
    /// <c>Harborline.Api.Kernel.Security.Crypto.IEd25519Signer.GenerateFromSeed</c> /
    /// <c>NodeIdentity.PrivateKey</c> carries, and the SAME 64-char-hex form the TS
    /// <c>NodeSigningKey.fromSeedHex</c> consumes (RFC 8410 §7 PKCS8 raw seed). A node, a
    /// .NET signer, and the TS signer therefore derive the SAME public key from the SAME seed.
    /// </para>
    /// <para>
    /// The seed is SECRET key material — never log it. The constructed key is import-with-
    /// <see cref="KeyExportPolicies.AllowPlaintextExport"/> so the public half re-exports;
    /// the secret is not exported by this class and is zeroed on <see cref="Dispose"/>.
    /// </para>
    /// </remarks>
    /// <param name="seed">The raw 32-byte Ed25519 seed (private key).</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="seed"/> is not exactly 32 bytes.</exception>
    public static KeyPair FromSeed(ReadOnlySpan<byte> seed)
    {
        if (seed.Length != PrincipalId.LengthInBytes)
        {
            throw new ArgumentException(
                $"Ed25519 seed must be exactly {PrincipalId.LengthInBytes} bytes (was {seed.Length}).",
                nameof(seed));
        }

        var creationParameters = new KeyCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport,
        };
        var key = Key.Import(SignatureAlgorithm.Ed25519, seed, KeyBlobFormat.RawPrivateKey, creationParameters);
        return new KeyPair(key);
    }

    /// <summary>The public-key identifier of this keypair.</summary>
    public PrincipalId PrincipalId => _principalId;

    /// <summary>Exposed to <see cref="Ed25519Signer"/> so it can call NSec's sign primitive directly.</summary>
    internal Key NSecKey => _key;

    /// <summary>
    /// Signs the supplied byte sequence with this keypair's Ed25519 private key.
    /// Used by wire-protocol layers (e.g. crew-comms HELLO/HEARTBEAT) where the
    /// signed bytes are NOT a canonical-JSON <see cref="SignedOperation{T}"/>
    /// envelope but a protocol-specific concatenation defined by the caller.
    /// </summary>
    /// <remarks>
    /// Use <see cref="Ed25519Signer"/> for ledger / audit-trail / cross-process
    /// envelopes that need <see cref="CanonicalJson"/> signing discipline.
    /// <see cref="Sign"/> exists exclusively for protocol-level signables that
    /// already define their own canonical byte stream.
    /// </remarks>
    public Signature Sign(ReadOnlySpan<byte> data)
    {
        Span<byte> signature = stackalloc byte[Signature.LengthInBytes];
        SignatureAlgorithm.Ed25519.Sign(_key, data, signature);
        return Signature.FromBytes(signature);
    }

    /// <summary>
    /// Verifies an Ed25519 signature over <paramref name="data"/> using a raw
    /// 32-byte public key. Returns <c>false</c> for malformed keys or invalid
    /// signatures. Symmetric companion to <see cref="Sign"/> for wire-protocol
    /// callers that have a public-key blob (not a <c>SignedOperation</c>).
    /// </summary>
    public static bool VerifyRaw(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        if (publicKey.Length != PrincipalId.LengthInBytes) return false;
        if (signature.Length != Signature.LengthInBytes) return false;
        try
        {
            var pk = PublicKey.Import(SignatureAlgorithm.Ed25519, publicKey, KeyBlobFormat.RawPublicKey);
            return SignatureAlgorithm.Ed25519.Verify(pk, data, signature);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public void Dispose() => _key.Dispose();
}
