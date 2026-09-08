using System;

using Harborline.Api.LocalNodeHost.Data.Comms;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// C2 — the conversation DESCRIPTOR (<see cref="CommsConversationDescriptor"/>): the unifying
/// <c>{ conversationId, kind, participantPartyIds, visibilityScope }</c> value the access + (later C5) sync
/// layers key off. Proves the team-channel descriptor (Team + AllTeam + all-team sentinel), the DM descriptor
/// (DirectMessage + Participants + exactly-2), and the <see cref="CommsConversationDescriptor.Validate"/>
/// invariant the arch-test fence asserts.
/// </summary>
public sealed class CommsConversationDescriptorTests
{
    private const string Team = "team-office";

    // ── TEAM descriptor: Team + AllTeam + the "all team" sentinel (empty participant set). ──────────────────

    [Fact(DisplayName = "C2: the team descriptor is Team + AllTeam + the all-team sentinel (empty participant set)")]
    public void Team_Descriptor_Is_AllTeam_With_Empty_Participants()
    {
        var d = CommsConversationDescriptor.Team();
        Assert.Equal(CommsConversation.TeamConversationId, d.ConversationId);
        Assert.Equal(CommsConversationKind.Team, d.Kind);
        Assert.Equal(CommsVisibilityScope.AllTeam, d.VisibilityScope);
        Assert.Empty(d.ParticipantPartyIds); // the roster is resolved LIVE — empty = "all team".
        d.Validate(); // well-formed
        // Any member is a participant of the team channel (AllTeam).
        Assert.True(d.IsParticipant("anyone"));
    }

    // ── DM descriptor: DirectMessage + Participants + EXACTLY the two parties (order-independent). ───────────

    [Fact(DisplayName = "C2: the DM descriptor carries kind=DirectMessage, Participants scope, and the two parties")]
    public void Dm_Descriptor_Carries_Kind_Participants_And_The_Two_Parties()
    {
        var d = CommsConversationDescriptor.DirectMessage(Team, "alice", "bob");
        Assert.Equal(CommsConversationKind.DirectMessage, d.Kind);
        Assert.Equal(CommsVisibilityScope.Participants, d.VisibilityScope);
        Assert.Equal(2, d.ParticipantPartyIds.Count);
        Assert.Contains("alice", d.ParticipantPartyIds);
        Assert.Contains("bob", d.ParticipantPartyIds);
        // The id matches the deterministic derivation.
        Assert.Equal(DmConversationId.Derive(Team, "alice", "bob"), d.ConversationId);
        d.Validate(); // well-formed
    }

    [Fact(DisplayName = "C2: the DM descriptor is order-independent (alice,bob == bob,alice)")]
    public void Dm_Descriptor_Is_Order_Independent()
    {
        var ab = CommsConversationDescriptor.DirectMessage(Team, "alice", "bob");
        var ba = CommsConversationDescriptor.DirectMessage(Team, "bob", "alice");
        Assert.Equal(ab.ConversationId, ba.ConversationId);
        Assert.Equal(ab.ParticipantPartyIds, ba.ParticipantPartyIds); // sorted → identical order
    }

    [Fact(DisplayName = "C2: IsParticipant is the v1 ACL — only the two DM parties; a third party is NOT a participant")]
    public void Dm_IsParticipant_Is_The_V1_Acl()
    {
        var d = CommsConversationDescriptor.DirectMessage(Team, "alice", "bob");
        Assert.True(d.IsParticipant("alice"));
        Assert.True(d.IsParticipant("bob"));
        Assert.False(d.IsParticipant("carol")); // the leak adversary — a team member who is NOT a DM participant.
    }

    // ── DescriptorFor: id → descriptor resolution (team always; DM with the pair). ───────────────────────────

    [Fact(DisplayName = "C2: DescriptorFor resolves the team id to the all-team descriptor")]
    public void DescriptorFor_Team_Resolves_AllTeam()
    {
        var d = CommsConversation.DescriptorFor(CommsConversation.TeamConversationId);
        Assert.Equal(CommsConversationKind.Team, d.Kind);
        Assert.Equal(CommsVisibilityScope.AllTeam, d.VisibilityScope);
    }

    [Fact(DisplayName = "C2: DescriptorFor resolves a dm: id to the participant-scoped descriptor when given the pair")]
    public void DescriptorFor_Dm_Resolves_When_Pair_Matches()
    {
        var id = DmConversationId.Derive(Team, "alice", "bob");
        var d = CommsConversation.DescriptorFor(id, Team, "alice", "bob");
        Assert.Equal(CommsConversationKind.DirectMessage, d.Kind);
        Assert.Equal(id, d.ConversationId);
    }

    [Fact(DisplayName = "C2: DescriptorFor REJECTS a dm: id when the supplied pair derives to a different id")]
    public void DescriptorFor_Dm_Rejects_Mismatched_Pair()
    {
        var id = DmConversationId.Derive(Team, "alice", "bob");
        // Supplying carol instead of bob derives a DIFFERENT id → mismatch → reject (the identity binds the pair).
        Assert.Throws<InvalidOperationException>(() => CommsConversation.DescriptorFor(id, Team, "alice", "carol"));
    }

    // ── Validate: the C2 invariant — malformed descriptors are rejected. ────────────────────────────────────

    [Fact(DisplayName = "C2: Validate rejects a DM descriptor with the wrong participant count")]
    public void Validate_Rejects_Wrong_Participant_Count()
    {
        var malformed = new CommsConversationDescriptor(
            "dm:abc", CommsConversationKind.DirectMessage, new[] { "alice" }, CommsVisibilityScope.Participants);
        Assert.Throws<InvalidOperationException>(() => malformed.Validate());
    }

    [Fact(DisplayName = "C2: Validate rejects a DM descriptor whose id lacks the dm: prefix")]
    public void Validate_Rejects_Dm_Without_Prefix()
    {
        var malformed = new CommsConversationDescriptor(
            "not-a-dm", CommsConversationKind.DirectMessage, new[] { "alice", "bob" }, CommsVisibilityScope.Participants);
        Assert.Throws<InvalidOperationException>(() => malformed.Validate());
    }

    [Fact(DisplayName = "C2: Validate rejects a DM descriptor with AllTeam visibility (a DM must be Participants)")]
    public void Validate_Rejects_Dm_With_AllTeam_Visibility()
    {
        var malformed = new CommsConversationDescriptor(
            "dm:abc", CommsConversationKind.DirectMessage, new[] { "alice", "bob" }, CommsVisibilityScope.AllTeam);
        Assert.Throws<InvalidOperationException>(() => malformed.Validate());
    }

    [Fact(DisplayName = "C2: Validate rejects a Team descriptor that carries a non-empty participant set")]
    public void Validate_Rejects_Team_With_Participants()
    {
        var malformed = new CommsConversationDescriptor(
            CommsConversation.TeamConversationId, CommsConversationKind.Team,
            new[] { "alice" }, CommsVisibilityScope.AllTeam);
        Assert.Throws<InvalidOperationException>(() => malformed.Validate());
    }
}
