namespace Harborline.Api.LocalNodeHost.Data.Comms;

/// <summary>
/// The synced projection of a single comms message — the value pushed, in author/insert order, into
/// a comms CRDT <b>list</b> (the first messaging doctype on the live node-host path; mirrors the contacts
/// doctype but swaps the <c>ICrdtMap</c> for an <c>ICrdtList</c>). One message = one append; the list is
/// immutable + append-only + ordered (no edit/delete in this pilot — append-only is the point, and is also
/// the GL's shape).
/// </summary>
/// <remarks>
/// <para>
/// <b>Conversation-scoped (C1).</b> <see cref="ConversationId"/> is the conversation a message belongs to —
/// the unit a chat UI calls "a room / a DM thread." Today there are two kinds: the well-known team channel
/// (<see cref="CommsConversation.TeamConversationId"/> = <c>"team"</c>, every roster member, plaintext) and,
/// later, 1:1 direct messages (a pair-derived <c>"dm:&lt;hash&gt;"</c> id, two participants, sealed body — C2+).
/// C1 ships ONLY the team conversation; the dimension exists so DMs are an additive change, not a rewrite.
/// A different conversation is a different append-log (a different CRDT document / sync stream). The
/// conversation is INSIDE the team scope: <see cref="TenantId"/> is the org, <see cref="ConversationId"/> is
/// the thread within that org.
/// </para>
/// <para>
/// <b>Per-author attribution is carried IN the value.</b> Unlike contacts (where the converging fields are
/// the contact's own data and the actor is the install-constant node operator), a comms message's load-
/// bearing identity is <em>who wrote it</em>: <see cref="AuthorPartyId"/> is the active member's id (the
/// ADR 0032 member's <c>ActorId</c>/<c>PartyId</c>, NOT a constant), and <see cref="AuthorIssuerId"/> +
/// <see cref="SignatureB64Url"/> bind the message to that author's signed identity
/// (<c>SignedOperation</c>/<c>IOperationSigner</c>, #1253). The signature proves message INTEGRITY and that
/// the holder of the stamped <see cref="AuthorIssuerId"/> key signed it; it does NOT prove the
/// <see cref="AuthorPartyId"/>↔<see cref="AuthorIssuerId"/> binding — any member can sign with their OWN key
/// and stamp another's <see cref="AuthorPartyId"/>, and it still verifies (#1277 B1b). Two authors who each
/// sign with their own key are distinguishable by issuer key on the converged log; forge-proof attribution
/// (the trusted party→key map) is LIVE in production via the seeded roster — see cerebrum [2026-06-20]. C1
/// adds <see cref="ConversationId"/> to the signable payload so the signature ALSO binds the conversation — a
/// message can't be replayed into another thread without re-signing.
/// </para>
/// <para>
/// <b>Flat serializable snapshot.</b> The CRDT list stores each item as its System.Text.Json form
/// (<c>ICrdtList.Push&lt;T&gt;</c> serializes; <c>Get&lt;T&gt;</c> deserializes), so this is deliberately a
/// flat record, not a domain aggregate. The signable payload the signature covers is
/// <see cref="SignablePayload"/> (a stable subset) so a peer can independently verify authorship.
/// </para>
/// </remarks>
/// <param name="MessageId">Stable per-message id (a Guid string); the append's identity across replicas.</param>
/// <param name="TenantId">The active-team-derived data tenant (ADR 0032) this message belongs to.</param>
/// <param name="ConversationId">The conversation (thread) within the team this message belongs to (C1; the well-known team channel is <see cref="CommsConversation.TeamConversationId"/>).</param>
/// <param name="AuthorPartyId">The appending member's id — the ADR 0032 active member's ActorId/PartyId.</param>
/// <param name="AuthorIssuerId">
/// The author's signing identity — base64url of the signer's public key (the verifier's trust anchor).
/// Identifies the SIGNING KEY that produced the signature; it is NOT bound to <see cref="AuthorPartyId"/>
/// (a member can sign with their own key + claim another's partyId — #1277 B1b). The key→party binding
/// is enforced by the roster-aware forge-proof merge gate (cerebrum [2026-06-20]).
/// </param>
/// <param name="AuthoredAtIso">Round-trippable ISO-8601 authoring instant (UTC).</param>
/// <param name="Body">The message text.</param>
/// <param name="NonceGuid">The signing nonce (string form) — part of the signed envelope, replay-defence at higher layers.</param>
/// <param name="SignatureB64Url">base64url Ed25519 signature over the canonical signable form of this message.</param>
public sealed record MessageCrdtState(
    string MessageId,
    string TenantId,
    string ConversationId,
    string AuthorPartyId,
    string AuthorIssuerId,
    string AuthoredAtIso,
    string Body,
    string NonceGuid,
    string SignatureB64Url)
{
    /// <summary>
    /// The stable signable subset the author's signature covers — the fields a peer canonicalizes + verifies
    /// to confirm authorship. Excludes the signature itself (which signs THIS) and the issuer id (which is
    /// the verifying key, passed alongside). Pinning the property names keeps the canonical bytes stable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>C1 — <see cref="ConversationId"/> is signed.</b> Binding the conversation into the signed payload
    /// means a message cannot be lifted from one thread and replayed into another without re-signing — the
    /// thread-replay defence the DM increments (C2+) rely on. Inserting <see cref="ConversationId"/> is a
    /// deliberate, one-time signable-shape change shipped with the additive migration. (Canonical-JSON sorts
    /// keys, so field declaration order does not affect the signed bytes; what changes is that a new
    /// <c>"ConversationId"</c> key now appears in the canonical form.)
    /// </para>
    /// <para>
    /// <b>Migration / re-verify safety.</b> Existing team rows are back-filled to <c>"team"</c> by the
    /// additive EF migration and are read by the GET path directly + re-seeded onto the CRDT by cold-start
    /// hydration — NEITHER path re-verifies an already-stored row (the merge gate runs ONLY on NEW inbound
    /// peer deltas, skipping ids already in EF). So a back-filled row is never re-verified against the new
    /// signable shape — the local team log is preserved intact. The ONE edge this does NOT cover is a
    /// transient MIXED-VERSION mesh: a message signed by a PRE-C1 peer (old binary, no
    /// <see cref="ConversationId"/> in its signed bytes) that arrives at a POST-C1 node would fail the
    /// forge-proof merge gate (the reconstructed signable now includes <c>ConversationId</c>) and be DROPPED
    /// — honest, fail-closed, and acceptable for the pilot (the cross-machine verify upgrades both binaries
    /// together; local-is-truth). This is NOT claimed as seamless cross-version interop.
    /// </para>
    /// </remarks>
    /// <param name="MessageId">The message id.</param>
    /// <param name="TenantId">The team tenant.</param>
    /// <param name="ConversationId">The conversation (thread) this message belongs to.</param>
    /// <param name="AuthorPartyId">The author's member id.</param>
    /// <param name="Body">The message body.</param>
    public sealed record SignablePayload(
        string MessageId,
        string TenantId,
        string ConversationId,
        string AuthorPartyId,
        string Body);

    /// <summary>The signable payload this message's signature attests.</summary>
    public SignablePayload ToSignable() => new(MessageId, TenantId, ConversationId, AuthorPartyId, Body);
}
