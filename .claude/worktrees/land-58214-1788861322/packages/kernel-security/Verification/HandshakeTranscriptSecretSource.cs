using System.Security.Cryptography;

namespace Harborline.Api.Kernel.Security.Verification;

/// <summary>
/// Channel-1 secret source over a completed ADR 0076 signed handshake (ADR 0152 §Item-(a)). The handshake
/// already signs each party's ephemeral X25519 pubkey with its Ed25519 identity, checks roster membership,
/// and agrees a DH secret both peers derive identically — so it IS a safety-number substrate.
/// </summary>
/// <remarks>
/// <para>
/// <b>THE LOAD-BEARING DERIVATION RULE (ADR 0152 BLOCKER-1 / F1 / F4; Admiral ruling 2026-07-16).</b> The
/// supplied material is the handshake's purpose-bound <c>VerificationSeed</c> — the additive
/// <c>HKDF-Expand-SHA256(ikm = DH shared secret, info = "sunfish-safety-code-seed-v1")</c> output the
/// <c>EncryptionHandshake</c> derives alongside its session key, from the SAME DH secret but under a
/// distinct HKDF label. It is therefore (a) a genuine authenticated shared secret, NOT the public
/// transcript — the transcript runs over the exchanged HELLO messages, which are PUBLIC wire data a
/// passive observer reproduces, so a transcript-derived code would verify for an eavesdropper (F1); and
/// (b) key-separated from the AEAD session key, so the spoken code can never be bytes of the encryption
/// key (F4). This source has no transcript parameter and never receives the session key, so neither F1
/// nor F4 value can enter the <c>ikm</c> position through it.
/// </para>
/// <para>
/// <b>The rule is structural, not documentary (ADR 0152 F1 / BLOCKER-1).</b> This source accepts ONLY a
/// <see cref="HandshakeVerificationSeed"/> — an opaque handle whose sole creation path consumes an
/// actual X25519 <c>SharedSecret</c>. It has no <c>byte[]</c> / <c>ReadOnlySpan&lt;byte&gt;</c>
/// constructor, so <c>new HandshakeTranscriptSecretSource(EncryptionHandshake.ComputeTranscriptHash(...))</c>
/// — which compiled and worked before, because the transcript hash is exactly 32 bytes and the old
/// constructor was gated only on length — is now a COMPILE ERROR. Neither an F1 (public transcript) nor
/// an F4 (session key) value can enter the <c>ikm</c> position through this source.
/// </para>
/// <para>
/// <b>Dependency direction.</b> <c>kernel-security</c> does not (and must not) reference
/// <c>blocks-crew-comms</c>; the crew-comms layer — which owns the <c>EncryptionHandshake</c> — depends on
/// this package and hands over the seed handle its handshake minted. The handshake uses
/// ephemeral-per-engagement DH keys, giving this source genuine forward secrecy and natural expiry when
/// the session ends (ADR 0152 §Item-(d), unlike <see cref="RosterEcdhSecretSource"/>).
/// </para>
/// </remarks>
public sealed class HandshakeTranscriptSecretSource : ISharedVerificationSecretSource, IDisposable
{
    private readonly byte[] _handshakeDerivedSecret;
    private readonly TimeProvider _timeProvider;
    private bool _disposed;

    /// <summary>
    /// Constructs the source over a completed handshake's verification seed. The seed's length and
    /// provenance are guaranteed by <see cref="HandshakeVerificationSeed"/>, so there is no length gate
    /// here to bypass — an unauthenticated value cannot be expressed as the parameter type at all.
    /// </summary>
    /// <param name="seed">
    /// The seed handle minted by the completed handshake. This source copies the material; the caller
    /// retains ownership of <paramref name="seed"/> and disposes it independently.
    /// </param>
    /// <param name="timeProvider">Clock for the fail-closed expiry gate; defaults to the system clock.</param>
    public HandshakeTranscriptSecretSource(
        HandshakeVerificationSeed seed,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(seed);

        _handshakeDerivedSecret = seed.Material.ToArray();
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <inheritdoc />
    public VerificationSecret? TryGetSharedSecret(VerificationContext ctx)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrEmpty(ctx.EngagementId))
        {
            return null;
        }

        if (ctx.Policy.Expired(_timeProvider.GetUtcNow()))
        {
            return null;
        }

        return new VerificationSecret(_handshakeDerivedSecret);
    }

    /// <summary>Zeroes the retained handshake secret.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CryptographicOperations.ZeroMemory(_handshakeDerivedSecret);
    }
}
