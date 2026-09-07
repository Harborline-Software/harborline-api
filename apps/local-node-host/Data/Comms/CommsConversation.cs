namespace Harborline.Api.LocalNodeHost.Data.Comms;

/// <summary>
/// The conversation-scope vocabulary for the comms doctype (C1) — the well-known team-channel id, the
/// conversation-kind classification by id prefix, and the C1 visibility/sealing facts the arch-test fence
/// asserts. A conversation is the unit a chat UI calls "a room / a DM thread": each is its own append-only,
/// signed, attributed message log (one CRDT document + one EF scope per conversation). C1 ships ONLY the team
/// channel; the prefixes for the later kinds (DM / group / channel) are reserved here so those increments are
/// additive, not a rewrite.
/// </summary>
/// <remarks>
/// <para>
/// <b>C1 scope.</b> Only <see cref="TeamConversationId"/> exists as a live conversation. The team channel is
/// PLAINTEXT and visible to the WHOLE team (behaviour-identical to the pre-C1 single team-wide log). The
/// reserved <c>dm:</c> / <c>grp:</c> / <c>chan:</c> prefixes carry NO behaviour in C1 — they exist so the
/// later increments (DM identity C2, encryption C4, participant-scoped routing C5) attach by id prefix
/// without re-shaping the doctype. The arch-test fence (<c>CommsConversationScopeArchTests</c>) asserts the
/// C1 facts and documents the C2–C5 extension points it will grow into.
/// </para>
/// <para>
/// <b>Why id-prefix classification.</b> A conversation's KIND (team vs DM vs group vs channel) — and therefore
/// its later access/seal/routing policy — is encoded in the id PREFIX, so the policy seam keys off the same
/// id the delta router + sync streams key off (the design's "make the wrong wiring impossible" property). The
/// team channel uses the bare well-known constant <c>"team"</c> (no prefix) so the pre-C1 default route is
/// preserved exactly.
/// </para>
/// </remarks>
public static class CommsConversation
{
    /// <summary>
    /// The well-known team-channel conversation id — the team-wide, plaintext, all-team append-log that is
    /// behaviour-identical to the pre-C1 single comms log. This is the conversation the bare
    /// <c>/api/local-node/comms</c> route (and the EF back-fill of pre-C1 rows) defaults to.
    /// </summary>
    public const string TeamConversationId = "team";

    /// <summary>Reserved id prefix for 1:1 direct messages (C2+: pair-derived id, two participants, SEALED body).</summary>
    public const string DirectMessagePrefix = "dm:";

    /// <summary>Reserved id prefix for group DMs (>2 participants; later — minted id + participant set).</summary>
    public const string GroupPrefix = "grp:";

    /// <summary>Reserved id prefix for named sub-channels (later — minted id + participant set + a name + PBAC).</summary>
    public const string ChannelPrefix = "chan:";

    /// <summary>
    /// True when <paramref name="conversationId"/> is the well-known team channel — the ONLY plaintext,
    /// all-team conversation kind in C1.
    /// </summary>
    public static bool IsTeam(string conversationId) =>
        string.Equals(conversationId, TeamConversationId, StringComparison.Ordinal);

    /// <summary>
    /// True when <paramref name="conversationId"/> names a 1:1 direct message (the <c>dm:</c> prefix). C1 has
    /// no live DM conversations — this classifier exists so the C4 seal / C5 participant-routing fences (and
    /// the arch-test extension point) can assert "a <c>dm:</c> conversation MUST be sealed + bounded-recipient"
    /// once those increments land.
    /// </summary>
    public static bool IsDirectMessage(string conversationId) =>
        conversationId is not null && conversationId.StartsWith(DirectMessagePrefix, StringComparison.Ordinal);

    /// <summary>
    /// Normalizes a route-supplied conversation id: a null/empty/whitespace id defaults to the team channel
    /// (so the bare <c>/api/local-node/comms</c> route and an empty path segment both target <c>"team"</c>,
    /// preserving pre-C1 behaviour). A non-empty id is returned trimmed.
    /// </summary>
    public static string Normalize(string? conversationId) =>
        string.IsNullOrWhiteSpace(conversationId) ? TeamConversationId : conversationId.Trim();

    /// <summary>
    /// The conversation KIND for an id, classified by its prefix (C2). The team channel is
    /// <see cref="CommsConversationKind.Team"/>; a <c>dm:</c>-prefixed id is
    /// <see cref="CommsConversationKind.DirectMessage"/>. (Group / channel kinds are reserved by their prefixes
    /// but not yet live, so they currently classify as DirectMessage only if dm:-prefixed — grp:/chan: are
    /// out of C2 scope and intentionally not mapped to a kind here.)
    /// </summary>
    public static CommsConversationKind KindOf(string conversationId) =>
        IsDirectMessage(conversationId) ? CommsConversationKind.DirectMessage : CommsConversationKind.Team;

    /// <summary>
    /// Build the C2 <see cref="CommsConversationDescriptor"/> for a known conversation id (C2). The team channel
    /// resolves to the all-team descriptor; a <c>dm:</c> id resolves to a participant-scoped descriptor whose
    /// two participants are supplied (a <c>dm:</c> hash is opaque — the participants are NOT recoverable from
    /// the id alone, so the caller, which derived the id from the pair or holds the conversation registration,
    /// passes them). Used to attach the descriptor's visibility/participant facts to a conversation for the
    /// access layer + the arch-test fence.
    /// </summary>
    /// <param name="conversationId">The conversation id (team or <c>dm:</c>).</param>
    /// <param name="teamId">The team the conversation is within (for re-deriving a DM id to verify the pair).</param>
    /// <param name="participantA">For a DM: one participant party id. Ignored for the team channel.</param>
    /// <param name="participantB">For a DM: the other participant party id. Ignored for the team channel.</param>
    /// <exception cref="InvalidOperationException">A <c>dm:</c> id whose derived id from the supplied pair does not match.</exception>
    public static CommsConversationDescriptor DescriptorFor(
        string conversationId, string? teamId = null, string? participantA = null, string? participantB = null)
    {
        var id = Normalize(conversationId);
        if (IsTeam(id))
        {
            return CommsConversationDescriptor.Team();
        }

        if (IsDirectMessage(id))
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(teamId);
            ArgumentException.ThrowIfNullOrWhiteSpace(participantA);
            ArgumentException.ThrowIfNullOrWhiteSpace(participantB);

            var descriptor = CommsConversationDescriptor.DirectMessage(teamId, participantA, participantB);
            // Defence: the supplied pair MUST derive to the supplied id (the deterministic identity binds the
            // descriptor to the id — a mismatch means a caller passed an inconsistent pair).
            if (!string.Equals(descriptor.ConversationId, id, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The participant pair derives to '{descriptor.ConversationId}', which does not match the conversation id '{id}'.");
            }
            return descriptor;
        }

        // grp:/chan: are reserved but not live in C2 — treat as an unsupported kind for descriptor resolution.
        throw new InvalidOperationException(
            $"Conversation id '{id}' is neither the team channel nor a 1:1 DM — group/channel descriptors are not part of C2.");
    }
}
