using System.Security.Cryptography;
using NSec.Cryptography;

namespace Harborline.Api.Kernel.Security.Verification;

/// <summary>
/// An opaque, single-provenance handle to the purpose-bound safety-code seed of a COMPLETED ADR 0076
/// signed handshake (ADR 0152 §Item-(a)). This is the ONLY value
/// <see cref="HandshakeTranscriptSecretSource"/> accepts.
/// </summary>
/// <remarks>
/// <para>
/// <b>THE LOAD-BEARING STRUCTURAL RULE (ADR 0152 BLOCKER-1 / F1).</b> A verification secret must never be
/// derivable from a raw handshake TRANSCRIPT: the transcript runs over the exchanged HELLO messages,
/// which are PUBLIC wire data a passive observer reproduces, so a transcript-derived code would verify
/// for an eavesdropper. Before this type existed the rule was enforced by an XML comment — every type on
/// the seam took a bare <c>byte[]</c>/<c>ReadOnlySpan&lt;byte&gt;</c>, and
/// <c>EncryptionHandshake.ComputeTranscriptHash(...)</c> returns exactly 32 bytes, so
/// <c>new HandshakeTranscriptSecretSource(ComputeTranscriptHash(...))</c> compiled and produced a working
/// source whose ikm was public wire data. This type makes that misuse a COMPILE ERROR.
/// </para>
/// <para>
/// <b>How the provenance gate works.</b> There is no public constructor and no public byte-export member,
/// so a seed cannot be manufactured from bytes the caller happens to hold. The single creation path,
/// <see cref="DeriveFrom"/>, consumes an <see cref="SharedSecret"/> — the output of an actual X25519 key
/// agreement. <see cref="SharedSecret"/> has NO public constructor, so a <c>byte[]</c> transcript hash
/// cannot become one by any implicit conversion. The seed therefore carries, in its type, the fact that
/// its ikm was a genuine agreed DH secret rather than public transcript bytes.
/// </para>
/// <para>
/// <b>Residual (disclosed, not hidden).</b> NSec exposes <c>SharedSecret.Import(...)</c> for interop.
/// A caller who deliberately writes
/// <c>SharedSecret.Import(transcriptHash, SharedSecretBlobFormat.NSecSharedSecret, in parameters)</c>
/// can still smuggle public bytes into the ikm position. That is an explicit, verbose, reviewable act —
/// not the silent one-expression accident this type closes. The fence is on CONSTRUCTION (provenance);
/// reading a legitimately obtained secret stays open because
/// <see cref="Keys.ISafetyCodeDerivation"/> needs the ikm.
/// </para>
/// <para>
/// <b>Single derivation site.</b> The frozen <see cref="InfoLabel"/> and the expansion live here and
/// ONLY here. <c>EncryptionHandshake</c> calls <see cref="DeriveFrom"/> rather than repeating the HKDF,
/// so the crypto identifier has exactly one definition and cannot drift between assemblies.
/// </para>
/// </remarks>
public sealed class HandshakeVerificationSeed : IDisposable
{
    /// <summary>
    /// HKDF <c>info</c> label that domain-separates the ADR 0152 safety-code seed from the AEAD session
    /// key derived from the same DH secret (F4 key separation). Part of the frozen
    /// <c>sunfish-*-v1</c> crypto-identifier namespace (ADR 0152 OQ-1) — DO NOT rename; it pins a
    /// cross-version-stable derivation.
    /// </summary>
    public const string InfoLabel = "sunfish-safety-code-seed-v1";

    /// <summary>Seed length in bytes; matches <see cref="VerificationSecret.MinimumLength"/> (256 bits).</summary>
    public const int LengthInBytes = 32;

    private static readonly byte[] InfoLabelBytes = "sunfish-safety-code-seed-v1"u8.ToArray();

    private readonly byte[] _material;
    private bool _disposed;

    private HandshakeVerificationSeed(byte[] material) => _material = material;

    /// <summary>
    /// Expands the safety-code seed from a completed handshake's X25519 shared secret.
    /// </summary>
    /// <param name="handshakeSharedSecret">
    /// The agreed DH secret of a completed ADR 0076 handshake. Because <see cref="SharedSecret"/> is only
    /// produced by an actual key agreement, a public transcript hash cannot be supplied here.
    /// </param>
    /// <param name="salt">
    /// The handshake's HKDF salt — the SAME salt used for the session key, so both outputs share one
    /// HKDF-Extract PRK and are separated purely by <see cref="InfoLabel"/> (F4). Public by design in
    /// HKDF; it is not the ikm and carries no secrecy requirement.
    /// </param>
    public static HandshakeVerificationSeed DeriveFrom(SharedSecret handshakeSharedSecret, ReadOnlySpan<byte> salt)
    {
        ArgumentNullException.ThrowIfNull(handshakeSharedSecret);

        var material = new byte[LengthInBytes];
        KeyDerivationAlgorithm.HkdfSha256.DeriveBytes(handshakeSharedSecret, salt, InfoLabelBytes, material);
        return new HandshakeVerificationSeed(material);
    }

    /// <summary>
    /// The seed bytes, readable ONLY inside this assembly (the reviewed secret sources). Deliberately not
    /// public: a public export would let the seed be copied into a bare buffer and defeat the provenance
    /// gate above. Throws once disposed.
    /// </summary>
    internal ReadOnlySpan<byte> Material
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _material;
        }
    }

    /// <summary>Zeroes the seed material.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CryptographicOperations.ZeroMemory(_material);
    }
}
