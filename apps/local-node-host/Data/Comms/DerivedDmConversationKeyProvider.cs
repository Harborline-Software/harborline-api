using System.Security.Cryptography;
using System.Text;

using NSec.Cryptography;

namespace Harborline.Api.LocalNodeHost.Data.Comms;

/// <summary>
/// The C4 default <see cref="IDmConversationKeyProvider"/> — derives the per-conversation AEAD key from an
/// X25519 ECDH shared secret between the two participants' DM subkeys, stretched by HKDF-SHA256 salted with the
/// conversation id (DR-2 Option A, the no-handshake derived construction):
/// <code>K_dm = HKDF-SHA256( ECDH(activeDmPriv, peerDmPub), salt = conversationId, info = "sunfish-dm-key:v1" )</code>
/// </summary>
/// <remarks>
/// <para>
/// <b>No-coordination by construction.</b> X25519 ECDH is symmetric — <c>ECDH(aPriv, bPub) == ECDH(bPriv, aPub)</c>
/// — so each participant independently computes the IDENTICAL shared secret, hence the identical <c>K_dm</c>,
/// with no key-exchange message. The conversation id (the deterministic dm: hash) is the HKDF SALT, so the key
/// is bound to the thread; the info string domain-separates this derivation from every other X25519 use.
/// </para>
/// <para>
/// <b>The leak property (DR-5) — depends on the resolver, NOT on this class alone.</b> A non-participant cannot
/// run this ECDH only if it cannot obtain a participant's DM PRIVATE key. The <c>me ∈ {A,B}</c> check below is an
/// IDENTITY/routing self-guard (it returns null when the active member is honestly not a participant), NOT a
/// cryptographic barrier — it can be bypassed by lying about who you are. The actual no-leak GUARANTEE rests on
/// the <see cref="IParticipantDmKeyResolver"/> keeping private keys node-secret + not derivable from a party id,
/// which is the C5 roster-bound resolver. With C4's fail-closed production provider
/// (<see cref="NoDmConversationKeyProvider"/>) no key is derivable at all. (sec-eng deep-review of PR #1325.)
/// </para>
/// <para>
/// <b>Roster-key binding (DR-2).</b> The peer's DM PUBLIC key comes from <see cref="IParticipantDmKeyResolver"/>.
/// In C5 that resolver reads the key from the verified team roster and binds the private key to the node's own
/// root seed, so the ECDH peer key is roster-bound (an attacker can't substitute their own key for a victim's) and
/// the private key is node-secret. C4 PRODUCTION wires no resolver (fail-closed); a test-only double exercises this
/// construction in the harness. Swapping the resolver in C5 does not change this derivation.
/// </para>
/// <para>
/// <b>Forward secrecy (honest limitation).</b> One long-term per-pair key (no ratchet) — v1-acceptable for a
/// local-office DM, documented in the ADR as the deferred Double-Ratchet/MLS hardening. The per-message random
/// nonce in <see cref="DmContentSeal"/> keeps each ciphertext distinct under the long-lived key.
/// </para>
/// </remarks>
public sealed class DerivedDmConversationKeyProvider : IDmConversationKeyProvider
{
    private static readonly KeyAgreementAlgorithm Kem = KeyAgreementAlgorithm.X25519;

    // Domain-separates the DM key derivation from every other X25519 use (recovery envelopes, team subkeys, …).
    private static readonly byte[] HkdfInfo = "sunfish-dm-key:v1"u8.ToArray();

    private readonly IParticipantDmKeyResolver _resolver;

    /// <summary>Constructs the provider over the participant DM-key resolver (the C4→C5 seam).</summary>
    public DerivedDmConversationKeyProvider(IParticipantDmKeyResolver resolver)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    /// <inheritdoc />
    public byte[]? TryDeriveConversationKey(string conversationId, string participantA, string participantB)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(participantA);
        ArgumentException.ThrowIfNullOrWhiteSpace(participantB);

        // Identify the PEER (the participant that is NOT the active member). The active member must be one of the
        // two participants — else this node does not attempt to derive the key (fail-closed routing).
        // NOTE: this is an IDENTITY/routing self-guard, NOT the cryptographic no-leak barrier. The real guarantee
        // (a non-participant cannot obtain the key even by forging a participant id) comes from the resolver keeping
        // private keys node-secret (C5 roster-bound); see the class remarks + sec-eng deep-review of PR #1325.
        var me = _resolver.ActiveMemberPartyId;
        string peer;
        if (string.Equals(me, participantA, StringComparison.Ordinal)) peer = participantB;
        else if (string.Equals(me, participantB, StringComparison.Ordinal)) peer = participantA;
        else return null; // the active member is not a participant of this DM — fail-closed routing (not a crypto guard).

        var peerPublicKey = _resolver.TryResolveDmPublicKey(peer);
        if (peerPublicKey is null || peerPublicKey.Length != 32) return null; // peer DM key not resolvable — fail-closed.

        var myPrivate = _resolver.ActiveMemberDmPrivateKey;
        if (myPrivate.Length != 32) return null;

        return DeriveKey(myPrivate, peerPublicKey, conversationId);
    }

    /// <summary>
    /// X25519(<paramref name="myPrivateKey"/>, <paramref name="peerPublicKey"/>) → HKDF-SHA256(salt =
    /// <paramref name="conversationId"/>) → 32-byte AEAD key.
    /// </summary>
    private static byte[] DeriveKey(ReadOnlySpan<byte> myPrivateKey, ReadOnlySpan<byte> peerPublicKey, string conversationId)
    {
        using var myKey = Key.Import(Kem, myPrivateKey, KeyBlobFormat.RawPrivateKey);
        var peer = PublicKey.Import(Kem, peerPublicKey, KeyBlobFormat.RawPublicKey);

        var sharedParams = default(SharedSecretCreationParameters);
        using var shared = Kem.Agree(myKey, peer, in sharedParams)
            ?? throw new CryptographicException(
                "X25519 DM key agreement failed (contributory check rejected the peer key).");

        var salt = Encoding.UTF8.GetBytes(conversationId);
        var aeadKey = new byte[DmContentSeal.KeyLength];
        KeyDerivationAlgorithm.HkdfSha256.DeriveBytes(shared, salt, HkdfInfo, aeadKey);
        return aeadKey;
    }
}
