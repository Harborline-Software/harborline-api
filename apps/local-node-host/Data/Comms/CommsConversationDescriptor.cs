namespace Harborline.Api.LocalNodeHost.Data.Comms;

/// <summary>The kind of a comms conversation — classifies its access/seal/routing policy (design §1.2).</summary>
public enum CommsConversationKind
{
    /// <summary>The well-known team channel — every roster member, plaintext, all-team fan-out.</summary>
    Team = 0,

    /// <summary>A 1:1 direct message — exactly two participants; SEALED body (C4) + participant-scoped routing (C5).</summary>
    DirectMessage = 1,
}

/// <summary>The visibility scope of a comms conversation — who may read/sync it (design §1.2 / §2).</summary>
public enum CommsVisibilityScope
{
    /// <summary>Visible to every member of the active team's roster (the team channel).</summary>
    AllTeam = 0,

    /// <summary>Visible ONLY to the bounded participant set (a DM's two parties).</summary>
    Participants = 1,
}

/// <summary>
/// The C2 conversation DESCRIPTOR — the unifying value the design (§1.2) names: a conversation is
/// <c>{ conversationId, kind, participantPartyIds, visibilityScope }</c>. C1 shipped the id + an id-prefix
/// classifier (<see cref="CommsConversation"/>); C2 adds the descriptor so the access + (later C5) sync layers
/// key off <see cref="ParticipantPartyIds"/> + <see cref="VisibilityScope"/> uniformly — the seam that makes
/// group/channel a later ADDITIVE change rather than a rewrite.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two kinds in C2.</b> The <b>team channel</b> (<see cref="Team"/>): <see cref="Kind"/> =
/// <see cref="CommsConversationKind.Team"/>, <see cref="VisibilityScope"/> =
/// <see cref="CommsVisibilityScope.AllTeam"/>, and an EMPTY <see cref="ParticipantPartyIds"/> (the team roster
/// is resolved LIVE, not stored — "all team" is the sentinel, design §1.2). A <b>1:1 DM</b>
/// (<see cref="DirectMessage"/>): <see cref="Kind"/> = <see cref="CommsConversationKind.DirectMessage"/>,
/// <see cref="VisibilityScope"/> = <see cref="CommsVisibilityScope.Participants"/>, and EXACTLY two distinct
/// <see cref="ParticipantPartyIds"/> (the two parties — derivable from the id, carried here so the access/seal
/// /routing layers read them without re-deriving).
/// </para>
/// <para>
/// <b>The C2 invariant the arch-test fences (design §4.2):</b> a <c>dm:</c> conversation MUST have
/// <see cref="Kind"/> = DirectMessage + <see cref="VisibilityScope"/> = Participants + EXACTLY 2 participant
/// party ids; the team channel MUST be Team + AllTeam + the "all team" sentinel (empty participant set). The
/// descriptor's factory methods (<see cref="Team"/> / <see cref="DirectMessage"/>) construct only well-formed
/// descriptors, and <see cref="Validate"/> re-asserts the invariant — "make the wrong wiring impossible."
/// </para>
/// <para>
/// <b>Visibility is honored end-to-end (C4 + C5 shipped).</b> The descriptor records the visibility INTENT for a
/// DM (<see cref="CommsVisibilityScope.Participants"/>); C4 sealed the body (content encryption) and C5 made the
/// keys roster-bound + node-secret and filtered the sync fan-out to participants. So a DM's intended visibility is
/// now enforced on the wire. The DM route surface SHIPS, gated by the <see cref="CommsDmFeatureFlag"/> (default ON,
/// kill-switch) — confidentiality rests on the C5 crypto, not on the route flag.
/// </para>
/// </remarks>
/// <param name="ConversationId">The conversation id — <c>"team"</c> for the team channel, <c>"dm:&lt;hash&gt;"</c> for a DM.</param>
/// <param name="Kind">The conversation kind (team vs direct message).</param>
/// <param name="ParticipantPartyIds">
/// The bounded participant set — EMPTY for the team channel (the "all team" sentinel; the roster is resolved
/// live), EXACTLY the two distinct parties for a DM. This is the v1 ACL for a DM (design §2.4).
/// </param>
/// <param name="VisibilityScope">Who may read/sync the conversation (all-team vs participants).</param>
public sealed record CommsConversationDescriptor(
    string ConversationId,
    CommsConversationKind Kind,
    IReadOnlyList<string> ParticipantPartyIds,
    CommsVisibilityScope VisibilityScope)
{
    /// <summary>The well-known team-channel descriptor — Team + AllTeam + the "all team" sentinel (empty set).</summary>
    public static CommsConversationDescriptor Team() => new(
        CommsConversation.TeamConversationId,
        CommsConversationKind.Team,
        Array.Empty<string>(),
        CommsVisibilityScope.AllTeam);

    /// <summary>
    /// The 1:1 DM descriptor for the UNORDERED pair (<paramref name="partyA"/>, <paramref name="partyB"/>)
    /// within <paramref name="teamId"/>. Derives the deterministic <c>dm:</c> id (<see cref="DmConversationId"/>)
    /// and records the two participants + the participant-only visibility scope. The participant set is stored
    /// in sorted order so it is order-independent (matching the id derivation).
    /// </summary>
    public static CommsConversationDescriptor DirectMessage(string teamId, string partyA, string partyB)
    {
        var conversationId = DmConversationId.Derive(teamId, partyA, partyB); // validates distinctness + non-empty
        var (first, second) = string.CompareOrdinal(partyA, partyB) <= 0 ? (partyA, partyB) : (partyB, partyA);
        return new CommsConversationDescriptor(
            conversationId,
            CommsConversationKind.DirectMessage,
            new[] { first, second },
            CommsVisibilityScope.Participants);
    }

    /// <summary>
    /// True if <paramref name="partyId"/> is a participant of this conversation. For a DM this is membership in
    /// <see cref="ParticipantPartyIds"/>; for the team channel it is always true for any member (the team
    /// roster — "all team" — but C2 does not resolve the live roster here, so this returns true for the team
    /// channel by the AllTeam scope). The (later C5) inbound fail-closed guard keys off this for DMs.
    /// </summary>
    public bool IsParticipant(string partyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partyId);
        return VisibilityScope switch
        {
            CommsVisibilityScope.AllTeam => true, // a team-channel message is readable by any team member.
            CommsVisibilityScope.Participants =>
                ParticipantPartyIds.Contains(partyId, StringComparer.Ordinal),
            _ => false,
        };
    }

    /// <summary>
    /// Re-assert the C2 well-formedness invariant (design §4.2). Throws if the descriptor is malformed — a
    /// belt-and-suspenders check the factory methods already satisfy, callable where a descriptor is assembled
    /// from external input. Returns the descriptor (fluent).
    /// </summary>
    /// <exception cref="InvalidOperationException">The descriptor violates the C2 conversation invariant.</exception>
    public CommsConversationDescriptor Validate()
    {
        switch (Kind)
        {
            case CommsConversationKind.Team:
                if (!CommsConversation.IsTeam(ConversationId))
                    throw new InvalidOperationException(
                        $"A Team conversation must use the well-known team id '{CommsConversation.TeamConversationId}', got '{ConversationId}'.");
                if (VisibilityScope != CommsVisibilityScope.AllTeam)
                    throw new InvalidOperationException("A Team conversation must have AllTeam visibility.");
                if (ParticipantPartyIds.Count != 0)
                    throw new InvalidOperationException(
                        "A Team conversation carries the 'all team' sentinel — an EMPTY participant set (the roster is resolved live).");
                break;

            case CommsConversationKind.DirectMessage:
                if (!CommsConversation.IsDirectMessage(ConversationId))
                    throw new InvalidOperationException(
                        $"A DirectMessage conversation id must start with '{CommsConversation.DirectMessagePrefix}', got '{ConversationId}'.");
                if (VisibilityScope != CommsVisibilityScope.Participants)
                    throw new InvalidOperationException("A DirectMessage conversation must have Participants visibility.");
                if (ParticipantPartyIds.Count != 2)
                    throw new InvalidOperationException(
                        $"A 1:1 DirectMessage conversation must have EXACTLY 2 participant party ids, got {ParticipantPartyIds.Count}.");
                if (string.Equals(ParticipantPartyIds[0], ParticipantPartyIds[1], StringComparison.Ordinal))
                    throw new InvalidOperationException("A 1:1 DirectMessage conversation's two participants must be DISTINCT.");
                break;

            default:
                throw new InvalidOperationException($"Unknown conversation kind '{Kind}'.");
        }

        return this;
    }
}
