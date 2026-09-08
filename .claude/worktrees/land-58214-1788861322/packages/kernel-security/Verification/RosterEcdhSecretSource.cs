using System.Security.Cryptography;
using NSec.Cryptography;

namespace Harborline.Api.Kernel.Security.Verification;

/// <summary>
/// Channel-1 secret source that REUSES the ADR 0136 co-roster construction: an X25519 ECDH between the
/// active member's signed-into-admission subkey and the counterparty's purpose-signed subkey, with no
/// handshake and zero coordination (ADR 0152 §Item-(a)). Both co-rostered parties independently compute
/// the identical secret.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors ADR 0136 <c>DerivedDmConversationKeyProvider</c>. X25519 ECDH is symmetric —
/// <c>ECDH(aPriv, bPub) == ECDH(bPriv, aPub)</c> — so each party derives the SAME shared secret. That
/// secret is engagement-INDEPENDENT here (the salt is empty); the per-engagement binding is added by
/// <see cref="Keys.SafetyCodeDerivation"/>, which salts the code with the engagement id.
/// </para>
/// <para>
/// <b>The load-bearing SUBSTITUTION defence (F2) is the resolver.</b> This source is only as strong as
/// <see cref="IVerificationSubkeyResolver.TryResolvePurposeSignedX25519PublicKey"/>: a non-interactive
/// ECDH with no proof-of-possession lets a MITM who SUBSTITUTES a party's public key compute the same
/// secret and pass verification. The resolver therefore surfaces a peer key ONLY when it is bound into
/// signed admission for this purpose; an unsigned / wrong-purpose key resolves to <c>null</c> and this
/// source fails closed. A missing private key (device locked) also fails closed.
/// </para>
/// <para>
/// <b>Forward secrecy (honest limitation, ADR 0152 §Item-(d)).</b> Roster subkeys are long-term, so this
/// source has NO forward secrecy from the keys themselves; expiry/revocation are policy-enforced (the
/// gate returns <c>null</c>) and a future roster-key compromise retroactively exposes past code
/// derivability — acceptable for a PUBLIC comparison value.
/// </para>
/// </remarks>
public sealed class RosterEcdhSecretSource : ISharedVerificationSecretSource
{
    private static readonly KeyAgreementAlgorithm Kem = KeyAgreementAlgorithm.X25519;

    /// <summary>
    /// Domain-separates the ECDH extract from every other X25519 use; part of the frozen
    /// <c>sunfish-*-v1:</c> namespace (ADR 0152 OQ-1).
    /// </summary>
    private static readonly byte[] ExtractInfo = "sunfish-safety-code-src-roster-ecdh-v1:"u8.ToArray();

    private const int KeyLength = 32;

    private readonly IVerificationSubkeyResolver _resolver;
    private readonly string _counterpartyPartyId;
    private readonly TimeProvider _timeProvider;

    /// <summary>Constructs a source for the single engagement with <paramref name="counterpartyPartyId"/>.</summary>
    public RosterEcdhSecretSource(
        IVerificationSubkeyResolver resolver,
        string counterpartyPartyId,
        TimeProvider? timeProvider = null)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        ArgumentException.ThrowIfNullOrWhiteSpace(counterpartyPartyId);
        _counterpartyPartyId = counterpartyPartyId;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Returns <c>null</c> for every ABSENCE: no engagement, an expired/revoked policy, a peer key the
    /// resolver refuses to surface (F2), or a missing private key (device locked). It does NOT return
    /// <c>null</c> when the peer key is present but DEGENERATE — X25519's contributory check rejecting
    /// it means the signed roster is carrying an invalid point, which is a structural anomaly rather
    /// than an absence, so it throws. See the interface remarks for why the two signals stay distinct.
    /// </remarks>
    /// <exception cref="CryptographicException">
    /// The resolved peer key failed X25519's contributory check (a low-order point). Loud by design:
    /// collapsing it into <c>null</c> would render corrupt or attacker-supplied signed roster data
    /// indistinguishable from a locked device.
    /// </exception>
    public VerificationSecret? TryGetSharedSecret(VerificationContext ctx)
    {
        if (string.IsNullOrEmpty(ctx.EngagementId))
        {
            return null;
        }

        if (ctx.Policy.Expired(_timeProvider.GetUtcNow()))
        {
            return null;
        }

        // F2: surfaced ONLY when signed-into-admission for THIS purpose; else fail-closed.
        var peerPublicKey = _resolver.TryResolvePurposeSignedX25519PublicKey(_counterpartyPartyId, ctx.Label);
        if (peerPublicKey is not { Length: KeyLength } peer)
        {
            return null;
        }

        var myPrivate = _resolver.ActiveMemberX25519PrivateKey;
        if (myPrivate.Length != KeyLength)
        {
            return null;
        }

        return Derive(myPrivate.Span, peer.Span);
    }

    private static VerificationSecret Derive(ReadOnlySpan<byte> myPrivate, ReadOnlySpan<byte> peerPublic)
    {
        using var myKey = Key.Import(Kem, myPrivate, KeyBlobFormat.RawPrivateKey);
        var peer = PublicKey.Import(Kem, peerPublic, KeyBlobFormat.RawPublicKey);

        var sharedParams = default(SharedSecretCreationParameters);
        using var shared = Kem.Agree(myKey, peer, in sharedParams)
            ?? throw new CryptographicException(
                "X25519 verification key agreement failed (contributory check rejected the peer key).");

        var secret = new byte[KeyLength];
        try
        {
            // HKDF-extract the non-exportable NSec shared secret into raw bytes. Empty salt keeps the
            // secret engagement-independent; SafetyCodeDerivation binds the engagement via its own salt.
            KeyDerivationAlgorithm.HkdfSha256.DeriveBytes(shared, Array.Empty<byte>(), ExtractInfo, secret);
            return new VerificationSecret(secret);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }
}
