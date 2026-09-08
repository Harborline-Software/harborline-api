namespace Harborline.Api.LocalNodeHost.Data.Comms;

/// <summary>
/// Yields the per-conversation symmetric AEAD key (32 bytes) that <see cref="DmContentSeal"/> seals/unseals a
/// 1:1 DM body with. The CONSTRUCTION is designed so the key is derivable only by the two participants — but that
/// guarantee holds only when the underlying <see cref="IParticipantDmKeyResolver"/> keeps DM PRIVATE keys
/// node-secret and NOT derivable from a public identifier. That is the C5 roster-bound resolver; until then,
/// production registers <see cref="NoDmConversationKeyProvider"/> (derives no key — fail-closed) and DMs are NOT
/// exposed (design §2.2, DR-2/DR-5; sec-eng deep-review of PR #1325).
/// </summary>
/// <remarks>
/// <para>
/// <b>The C4 construction (derived-ECDH, no handshake — DR-2 Option A).</b> The default implementation
/// (<see cref="DerivedDmConversationKeyProvider"/>) derives the key from an X25519 ECDH shared secret between
/// the two participants' DM-encryption subkeys, stretched by HKDF salted with the conversation id:
/// <code>K_dm = HKDF-SHA256( ECDH(myDmPriv, theirDmPub), salt = conversationId, info = domain )</code>
/// ECDH is symmetric, so each participant independently computes the SAME key with ZERO coordination (the
/// no-handshake property — fits the no-coordination DM). A non-participant cannot run the ECDH (it lacks either
/// participant's private DM key) and so cannot derive <c>K_dm</c>.
/// </para>
/// <para>
/// <b>The seam shape.</b> This interface takes the conversation id + the ORDERED participant pair (so the caller
/// — which already holds the descriptor or derived the id — supplies the two parties; the dm: hash is opaque).
/// Returning <c>null</c> means "this node cannot derive the key for this conversation" (the active member is not
/// a participant, or a participant's DM public key is not resolvable) — the caller then treats the body as
/// opaque (leaves it sealed; never plaintext). This is the same fail-closed posture the merge gate uses.
/// </para>
/// <para>
/// <b>C4/C5 boundary.</b> The participant DM PUBLIC keys this provider needs are resolved by
/// <see cref="IParticipantDmKeyResolver"/>. C4 PRODUCTION registers no resolver — it uses
/// <see cref="NoDmConversationKeyProvider"/> (fail-closed). The construction is exercised in the TEST harness by a
/// test-only resolver (party-id-derived keys; NOT a secrecy boundary). C5 wires the resolver to the verified team
/// roster — each node generates its DM private key from its OWN root seed (node-secret, NOT party-id-derivable) and
/// publishes the PUBLIC key on the enrollment wire — which is where the no-leak GUARANTEE + roster-key binding +
/// participant-scoped routing land. The key CONSTRUCTION here does not change when C5 swaps the resolver — only
/// WHERE the peer public key comes from and how the private key is bound.
/// </para>
/// </remarks>
public interface IDmConversationKeyProvider
{
    /// <summary>
    /// Derive the per-conversation AEAD key for <paramref name="conversationId"/> between the active member and
    /// the other participant. Returns a 32-byte key the two participants share, or <c>null</c> if this node
    /// cannot derive it (the active member is not a participant, or the peer DM public key is unresolvable).
    /// </summary>
    /// <param name="conversationId">The DM conversation id (the HKDF salt — binds the key to this thread).</param>
    /// <param name="participantA">One participant party id.</param>
    /// <param name="participantB">The other participant party id.</param>
    byte[]? TryDeriveConversationKey(string conversationId, string participantA, string participantB);
}

/// <summary>
/// Null-object <see cref="IDmConversationKeyProvider"/> — derives NO keys. Registered when the host cannot build
/// the real provider (a minimal / team-only test host with no roster or root seed), so DI always resolves a
/// non-null provider and a dm: projection simply has no key (every body opaque, no sealing) without special-casing
/// null. The team channel is unaffected (it never seals).
/// </summary>
public sealed class NoDmConversationKeyProvider : IDmConversationKeyProvider
{
    /// <inheritdoc />
    public byte[]? TryDeriveConversationKey(string conversationId, string participantA, string participantB) => null;
}

/// <summary>
/// Resolves the X25519 DM-encryption PUBLIC key for a participant party id, and exposes the active member's
/// identity + DM private key — the material <see cref="DerivedDmConversationKeyProvider"/> runs the ECDH over.
/// This is the C4→C5 seam. <b>No production implementation exists in C4</b> (production is fail-closed via
/// <see cref="NoDmConversationKeyProvider"/> — an arch-fence asserts the shipped assembly registers no concrete
/// resolver). A party-id-derived TEST-ONLY double exercises the construction in the harness. C5 wires a
/// roster-backed resolver that reads each member's DM public key from the verified team roster and binds the
/// private key to the node's own root seed (node-secret, not party-id-derivable) — the increment that delivers
/// the no-leak guarantee.
/// </summary>
public interface IParticipantDmKeyResolver
{
    /// <summary>The active (local) member's party id — the "me" in the ECDH.</summary>
    string ActiveMemberPartyId { get; }

    /// <summary>The active member's 32-byte X25519 DM PRIVATE key (never leaves the node).</summary>
    ReadOnlySpan<byte> ActiveMemberDmPrivateKey { get; }

    /// <summary>
    /// The 32-byte X25519 DM PUBLIC key bound to <paramref name="partyId"/>, or <c>null</c> if it is not
    /// resolvable (not a known participant / not yet distributed). Fail-closed: an unresolvable peer key means
    /// no key can be derived, so the body stays sealed (opaque).
    /// </summary>
    byte[]? TryResolveDmPublicKey(string partyId);
}
