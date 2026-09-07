using Harborline.Api.Foundation.Crypto;

namespace Harborline.Api.LocalNodeHost.Data.Comms;

/// <summary>
/// Builds + verifies the SIGNED, attributed <see cref="MessageCrdtState"/> for a comms append. The single
/// source of truth for how a message carries its author's signed identity: an author signs the canonical
/// signable form of <c>{messageId, tenantId, authorPartyId, body}</c> with their
/// <see cref="IOperationSigner"/>, and the resulting <see cref="SignedOperation{T}"/> envelope's issuer +
/// signature are stamped onto the message.
/// </summary>
/// <remarks>
/// <b>SECURITY — the two verification tiers (#1277 B1).</b> The bare signature proves message INTEGRITY (the
/// signed fields were not altered) and that the holder of the stamped <c>AuthorIssuerId</c> key signed it. It
/// does NOT, by itself, prove the <c>AuthorPartyId</c>↔<c>AuthorIssuerId</c> binding: any member can sign with
/// their OWN key and stamp another member's <c>AuthorPartyId</c>, and the bare signature still verifies (their
/// key over a payload that happens to name the other party) — the #1277 B1a tier (the 2-arg
/// <see cref="VerifyAuthorship(MessageCrdtState, IOperationVerifier)"/>). The party↔key binding IS proven by
/// the 3-arg forge-proof overload (<see cref="VerifyAuthorship(MessageCrdtState, IOperationVerifier,
/// Func{string, PrincipalId?})"/>), which checks the stamped key against the trust roster's bound key for the
/// claimed party. Enrollment Phase B WIRES that overload into the production merge gate (the seeded
/// <c>NodeTeamRoster</c> → <c>CommsCrdtProjection</c>'s <c>rosterBinding</c>), so a forged party claim is
/// DROPPED in production — #1277 B1b CLOSED end-to-end (cerebrum [2026-06-20]). Only describe the bare 2-arg
/// check as verifiable INTEGRITY + key-holder; the forge-proof party binding is the 3-arg overload.
/// </remarks>
/// <remarks>
/// Used by <c>CommsRoutes</c> (the live append path, signing with the node's canonical operator identity)
/// and by the convergence test (two distinct author keypairs). Keeping it here means the route and the test
/// build attribution identically.
/// </remarks>
public static class CommsMessageFactory
{
    /// <summary>
    /// Create a signed, attributed message. The signature covers the canonical-JSON signable form
    /// (<see cref="MessageCrdtState.SignablePayload"/>) so a peer holding the author's public key
    /// (<see cref="MessageCrdtState.AuthorIssuerId"/>) can independently verify authorship via
    /// <see cref="VerifyAuthorship"/>.
    /// </summary>
    /// <param name="signer">The author's operation signer (their identity).</param>
    /// <param name="authorPartyId">The author's member id (the ADR 0032 active member's ActorId/PartyId).</param>
    /// <param name="tenantId">The active-team-derived data tenant (ADR 0032).</param>
    /// <param name="body">The message text.</param>
    /// <param name="authoredAt">The authoring instant (UTC).</param>
    /// <param name="conversationId">
    /// The conversation (thread) the message belongs to (C1). Defaults to the well-known team channel
    /// (<see cref="CommsConversation.TeamConversationId"/>) so existing call-sites that don't pass a
    /// conversation append to the team log exactly as before. The conversation is part of the SIGNED payload
    /// (a message can't be replayed into another thread).
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public static async ValueTask<MessageCrdtState> CreateSignedAsync(
        IOperationSigner signer,
        string authorPartyId,
        string tenantId,
        string body,
        DateTimeOffset authoredAt,
        string conversationId = CommsConversation.TeamConversationId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(signer);
        ArgumentException.ThrowIfNullOrWhiteSpace(authorPartyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentNullException.ThrowIfNull(body);

        var messageId = Guid.NewGuid().ToString("D");
        var nonce = Guid.NewGuid();

        // The canonical signable form emits issuedAt as Unix epoch-MILLISECONDS (CanonicalJson, #1254), so
        // truncate to ms BEFORE signing. Otherwise the stored ISO string (sub-ms "O" form) would, when
        // re-parsed at verify time, still re-truncate to the same epoch-ms — but truncating here keeps the
        // stored ISO and the signed instant exactly aligned, so VerifyAuthorship reconstructs identical
        // signable bytes with no precision drift.
        var authoredAtMs = DateTimeOffset.FromUnixTimeMilliseconds(authoredAt.ToUnixTimeMilliseconds());
        var signable = new MessageCrdtState.SignablePayload(
            messageId, tenantId, conversationId, authorPartyId, body);
        var op = await signer.SignAsync(signable, authoredAtMs, nonce, ct).ConfigureAwait(false);

        return new MessageCrdtState(
            MessageId: messageId,
            TenantId: tenantId,
            ConversationId: conversationId,
            AuthorPartyId: authorPartyId,
            AuthorIssuerId: op.IssuerId.ToBase64Url(),
            AuthoredAtIso: authoredAtMs.ToString("O"),
            Body: body,
            NonceGuid: nonce.ToString("D"),
            SignatureB64Url: op.Signature.ToBase64Url());
    }

    /// <summary>
    /// Create a signed, SEALED DM message (C4) — the sign-then-encrypt construction (DR-3). The signature covers
    /// the canonical-JSON signable form over the PLAINTEXT body (authorship is proven over the decrypted content,
    /// so a participant verifies who wrote what they read), and the returned message's <c>Body</c> is the SEALED
    /// ciphertext envelope (<see cref="DmContentSeal"/>) bound to the per-conversation key — so a non-participant
    /// (incl. a team member on the same plane) holds only opaque ciphertext (design §2.2, DR-5). The seal's AEAD
    /// AAD binds <c>{conversationId, tenantId, authorPartyId, messageId}</c> (DR-3), so the ciphertext cannot be
    /// lifted/replayed into another thread/author/message without the tag failing.
    /// </summary>
    /// <param name="signer">The author's operation signer (their identity).</param>
    /// <param name="authorPartyId">The author's member id (ADR 0032 active member's ActorId/PartyId).</param>
    /// <param name="tenantId">The active-team-derived data tenant (ADR 0032).</param>
    /// <param name="plaintextBody">The DM message text (signed in the clear, sealed in the body).</param>
    /// <param name="authoredAt">The authoring instant (UTC).</param>
    /// <param name="conversationId">The DM conversation id (a <c>dm:</c> id — in the signed payload AND the seal AAD).</param>
    /// <param name="conversationKey">The 32-byte per-conversation AEAD key (only the two participants can derive it).</param>
    /// <param name="ct">Cancellation token.</param>
    public static async ValueTask<MessageCrdtState> CreateSignedSealedAsync(
        IOperationSigner signer,
        string authorPartyId,
        string tenantId,
        string plaintextBody,
        DateTimeOffset authoredAt,
        string conversationId,
        ReadOnlyMemory<byte> conversationKey,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(signer);
        ArgumentException.ThrowIfNullOrWhiteSpace(authorPartyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentNullException.ThrowIfNull(plaintextBody);
        if (!CommsConversation.IsDirectMessage(conversationId))
            throw new ArgumentException(
                $"Only a DM conversation ('{CommsConversation.DirectMessagePrefix}…') is sealed; '{conversationId}' is not.",
                nameof(conversationId));

        var messageId = Guid.NewGuid().ToString("D");
        var nonce = Guid.NewGuid();
        var authoredAtMs = DateTimeOffset.FromUnixTimeMilliseconds(authoredAt.ToUnixTimeMilliseconds());

        // DR-3: SIGN the PLAINTEXT signable FIRST (authorship over decrypted content), THEN seal the body.
        var signable = new MessageCrdtState.SignablePayload(
            messageId, tenantId, conversationId, authorPartyId, plaintextBody);
        var op = await signer.SignAsync(signable, authoredAtMs, nonce, ct).ConfigureAwait(false);

        // Seal the body to ciphertext, binding the message context as AEAD AAD (DR-3 — context-bound ciphertext).
        var sealedBody = DmContentSeal.Seal(
            plaintextBody, conversationKey.Span, conversationId, tenantId, authorPartyId, messageId);

        return new MessageCrdtState(
            MessageId: messageId,
            TenantId: tenantId,
            ConversationId: conversationId,
            AuthorPartyId: authorPartyId,
            AuthorIssuerId: op.IssuerId.ToBase64Url(),
            AuthoredAtIso: authoredAtMs.ToString("O"),
            Body: sealedBody, // CIPHERTEXT on the wire + at rest — the signed payload covered the PLAINTEXT.
            NonceGuid: nonce.ToString("D"),
            SignatureB64Url: op.Signature.ToBase64Url());
    }

    /// <summary>
    /// Verify authorship of a SEALED DM message against its UNSEALED plaintext (C4). A sealed message's stored
    /// <c>Body</c> is ciphertext, so <see cref="VerifyAuthorship(MessageCrdtState, IOperationVerifier)"/> (which
    /// reconstructs the signable from the stored body) cannot verify it directly — the signature covered the
    /// PLAINTEXT (DR-3 sign-then-encrypt). A PARTICIPANT first unseals the body, then calls this with the
    /// recovered plaintext to verify authorship over the decrypted content. A non-participant cannot unseal, so it
    /// never reaches this check — it holds only opaque ciphertext.
    /// </summary>
    /// <param name="message">The sealed message (its <c>Body</c> is the sealed envelope).</param>
    /// <param name="plaintextBody">The plaintext recovered from <see cref="DmContentSeal.TryUnseal"/>.</param>
    /// <param name="verifier">The Ed25519 signature verifier.</param>
    /// <param name="boundKeyFor">
    /// The roster party→key resolver (forge-proof, #1277 B1b). Required: a DM is between enrolled members, so the
    /// author MUST be roster-bound — fail-closed for an un-enrolled author.
    /// </param>
    public static bool VerifySealedAuthorship(
        MessageCrdtState message,
        string plaintextBody,
        IOperationVerifier verifier,
        Func<string, PrincipalId?> boundKeyFor)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(plaintextBody);
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(boundKeyFor);

        // Reconstruct the message AS IF the body were the recovered plaintext, then run the standard forge-proof
        // gate — the signature attested the plaintext, so this verifies authorship over the decrypted content.
        var asPlaintext = message with { Body = plaintextBody };
        return VerifyAuthorship(asPlaintext, verifier, boundKeyFor);
    }

    /// <summary>
    /// Integrity-only sealed-authorship check (no roster) — the B1a tier for the sealed body. Verifies the
    /// signature over the recovered plaintext against the stamped key; used by tests/dev paths with no roster
    /// binding wired.
    /// </summary>
    public static bool VerifySealedAuthorship(
        MessageCrdtState message,
        string plaintextBody,
        IOperationVerifier verifier)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(plaintextBody);
        ArgumentNullException.ThrowIfNull(verifier);
        var asPlaintext = message with { Body = plaintextBody };
        return VerifyAuthorship(asPlaintext, verifier);
    }

    /// <summary>
    /// Re-verify a message's signature: reconstructs the signed envelope from the stored fields and checks
    /// the Ed25519 signature against the STAMPED author public key (<see cref="MessageCrdtState.AuthorIssuerId"/>).
    /// Returns <c>true</c> iff the signature is a valid signature by that key over this message's signable
    /// form — so a tampered body / id fails, AND re-attributing an already-signed message to a different
    /// <c>AuthorPartyId</c> without re-signing fails (the partyId is inside the signed payload).
    /// </summary>
    /// <remarks>
    /// SECURITY: this 2-arg overload does NOT verify the party↔key binding — it confirms the holder of the
    /// STAMPED key signed the message, NOT that the stamped key belongs to the claimed <c>AuthorPartyId</c>. A
    /// member who signs a FRESH message with their own key while stamping another member's <c>AuthorPartyId</c>
    /// still passes here (their valid sig over a payload naming the other party). The 3-arg forge-proof overload
    /// below CLOSES that, and enrollment Phase B wires it into the production merge gate (#1277 B1b CLOSED
    /// end-to-end; cerebrum [2026-06-20]). Use this 2-arg form only where there is no adversary among your own
    /// nodes (a single-/shared-root pilot); the production comms path uses the 3-arg form.
    /// </remarks>
    public static bool VerifyAuthorship(MessageCrdtState message, IOperationVerifier verifier)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(verifier);

        try
        {
            var op = new SignedOperation<MessageCrdtState.SignablePayload>(
                Payload: message.ToSignable(),
                IssuerId: PrincipalId.FromBase64Url(message.AuthorIssuerId),
                IssuedAt: DateTimeOffset.Parse(message.AuthoredAtIso, null,
                    System.Globalization.DateTimeStyles.RoundtripKind),
                Nonce: Guid.Parse(message.NonceGuid),
                Signature: Signature.FromBase64Url(message.SignatureB64Url));
            return verifier.Verify(op);
        }
        catch (FormatException)
        {
            // Malformed issuer / signature / nonce → not verifiable → not authentic.
            return false;
        }
    }

    /// <summary>
    /// FORGE-PROOF authorship verification (closes #1277 B1; enrollment Phase A). The payoff of the trust
    /// roster: in addition to the integrity + key-holder check (<see cref="VerifyAuthorship(MessageCrdtState,
    /// IOperationVerifier)"/>), this asserts the message's STAMPED signing key
    /// (<see cref="MessageCrdtState.AuthorIssuerId"/>) is the key the ROSTER binds to the claimed
    /// <see cref="MessageCrdtState.AuthorPartyId"/>. An attacker who signs with their OWN key while stamping a
    /// victim's partyId now FAILS — their issuer key ≠ the victim's roster-bound key.
    /// </summary>
    /// <param name="message">The message to verify.</param>
    /// <param name="verifier">The Ed25519 signature verifier (the integrity + key-holder check).</param>
    /// <param name="boundKeyFor">
    /// The roster's party→pubkey binding resolver: returns the <see cref="PrincipalId"/> the verified roster
    /// binds to a given party id, or <c>null</c> when the party is NOT a roster member. Typically
    /// <c>MemberRoster.PublicKeyOf</c>. A message whose <see cref="MessageCrdtState.AuthorPartyId"/> is not in
    /// the roster FAILS (fail-closed — an un-enrolled author cannot be attributed).
    /// </param>
    /// <returns>
    /// <c>true</c> iff the signature is valid for the stamped key AND that key is exactly the roster-bound key
    /// for the claimed party. <c>false</c> on a forged party claim, an un-enrolled party, or an invalid
    /// signature.
    /// </returns>
    public static bool VerifyAuthorship(
        MessageCrdtState message,
        IOperationVerifier verifier,
        Func<string, PrincipalId?> boundKeyFor)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(boundKeyFor);

        // 1) Integrity + key-holder: the stamped key validly signed this exact message.
        if (!VerifyAuthorship(message, verifier))
        {
            return false;
        }

        // 2) PARTY↔KEY BINDING: the stamped issuer key MUST be the roster's bound key for the claimed party.
        //    This is the gap #1277 B1b named — and the payoff of the roster.
        var boundKey = boundKeyFor(message.AuthorPartyId);
        if (boundKey is null)
        {
            // The claimed author is not an enrolled roster member — cannot attribute. Fail-closed.
            return false;
        }

        try
        {
            var stampedKey = PrincipalId.FromBase64Url(message.AuthorIssuerId);
            return stampedKey.Equals(boundKey.Value);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
