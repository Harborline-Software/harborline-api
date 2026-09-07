using System.Security.Cryptography;

namespace Harborline.Api.Kernel.Security.Verification;

/// <summary>
/// Authenticated shared bytes available IDENTICALLY to both parties of a verification (ADR 0152
/// §Item-(a)). Fed as the HKDF <c>ikm</c> to <see cref="Keys.ISafetyCodeDerivation.Derive"/>.
/// </summary>
/// <remarks>
/// <para>
/// Owns a private copy of the secret material and zeroes it on <see cref="Dispose"/>. Callers dispose
/// the returned instance once a code has been derived — the code is public, the secret is not, and the
/// secret must not outlive the derivation.
/// </para>
/// <para>
/// The material MUST be a genuine authenticated shared secret (a completed ADR 0076 handshake secret or
/// a purpose-signed co-roster ECDH). A public value — most importantly an ADR 0076 handshake TRANSCRIPT
/// hash — is NOT a secret and must never be wrapped here (ADR 0152 F1 / BLOCKER-1). The ≥256-bit minimum
/// is enforced so a truncated or absent secret fails closed rather than yielding a weak code.
/// </para>
/// <para>
/// <b>That rule is enforced by the type system, not by this comment (ADR 0152 F1 / BLOCKER-1).</b> The
/// constructor is <c>internal</c>, so ONLY the reviewed secret sources in this assembly can mint a
/// verification secret — no caller anywhere else can wrap bytes it happens to hold (a transcript hash,
/// a party id, a session key) as an authenticated shared secret. The shipped source set is itself pinned
/// by <c>VerificationArchFenceTests</c>, so every <see cref="VerificationSecret"/> in the system provably
/// originates from a source that has been through security review. Reading
/// <see cref="Material"/> stays public because <see cref="Keys.ISafetyCodeDerivation"/> consumes it as
/// the HKDF ikm — the fence is on construction (provenance), not on reading.
/// </para>
/// </remarks>
public sealed class VerificationSecret : IDisposable
{
    /// <summary>Minimum authenticated shared-secret length: 256 bits.</summary>
    public const int MinimumLength = 32;

    private readonly byte[] _material;
    private bool _disposed;

    /// <summary>
    /// Copies <paramref name="material"/> (≥ <see cref="MinimumLength"/> bytes) into an owned buffer.
    /// INTERNAL BY DESIGN — see the F1 note on the type. Widening this to <c>public</c> re-opens
    /// ADR 0152 BLOCKER-1 and is pinned against by
    /// <c>VerificationArchFenceTests.Verification_secret_exposes_no_public_constructor</c>.
    /// </summary>
    internal VerificationSecret(ReadOnlySpan<byte> material)
    {
        if (material.Length < MinimumLength)
        {
            throw new ArgumentException(
                $"Authenticated shared secret must contain at least {MinimumLength} bytes.",
                nameof(material));
        }

        _material = material.ToArray();
    }

    /// <summary>The authenticated shared bytes. Throws once disposed.</summary>
    public ReadOnlyMemory<byte> Material
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _material;
        }
    }

    /// <inheritdoc />
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
