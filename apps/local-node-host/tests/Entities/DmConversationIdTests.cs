using System;

using Harborline.Api.LocalNodeHost.Data.Comms;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// C2 — the deterministic DM conversation-id derivation (<see cref="DmConversationId"/>). The load-bearing
/// proof: both participants A and B independently compute the SAME <c>dm:</c> id from the UNORDERED party pair
/// with ZERO coordination (the no-create-handshake property, design §1.3). Plus: team-salt isolation, the
/// <c>dm:</c> prefix + classification, self-DM rejection, and concat-ambiguity resistance.
/// </summary>
public sealed class DmConversationIdTests
{
    private const string Team = "team-office";

    // ── THE A==B PROOF: A and B independently derive the SAME id (order-independent, no coordination). ──────

    [Fact(DisplayName = "C2: A and B independently compute the IDENTICAL dm: id (order-independent — no handshake)")]
    public void A_And_B_Independently_Compute_The_Same_Id()
    {
        // A computes the id for (self=alice, other=bob); B computes it for (self=bob, other=alice). The args are
        // SWAPPED — exactly what each end does when it derives "my DM with the other person." They MUST match.
        var idComputedByA = DmConversationId.Derive(Team, "alice", "bob");
        var idComputedByB = DmConversationId.Derive(Team, "bob", "alice");

        Assert.Equal(idComputedByA, idComputedByB); // SAME id, zero coordination — the no-handshake property.
        Assert.StartsWith(CommsConversation.DirectMessagePrefix, idComputedByA);
        Assert.True(CommsConversation.IsDirectMessage(idComputedByA));
        Assert.False(CommsConversation.IsTeam(idComputedByA));
    }

    [Fact(DisplayName = "C2: the dm: id is deterministic across repeated derivations (stable)")]
    public void Id_Is_Deterministic_Across_Calls()
    {
        var first = DmConversationId.Derive(Team, "alice", "bob");
        var second = DmConversationId.Derive(Team, "alice", "bob");
        Assert.Equal(first, second); // same inputs → same id, every time (a node restart re-derives identically).
    }

    // ── TEAM-SALT ISOLATION: the same pair in two orgs gets two distinct threads. ───────────────────────────

    [Fact(DisplayName = "C2: the same pair in two different teams derives DISTINCT dm: ids (org isolation)")]
    public void Same_Pair_Different_Team_Yields_Distinct_Ids()
    {
        var inTeamA = DmConversationId.Derive("team-acme", "alice", "bob");
        var inTeamB = DmConversationId.Derive("team-globex", "alice", "bob");
        Assert.NotEqual(inTeamA, inTeamB); // team-scoped salt → distinct DM threads per org.
    }

    [Fact(DisplayName = "C2: distinct pairs derive distinct dm: ids")]
    public void Distinct_Pairs_Yield_Distinct_Ids()
    {
        var aliceBob = DmConversationId.Derive(Team, "alice", "bob");
        var aliceCarol = DmConversationId.Derive(Team, "alice", "carol");
        Assert.NotEqual(aliceBob, aliceCarol);
    }

    // ── FORMAT: dm: prefix + fixed-length opaque base32 hash (no party id leaks in the id). ──────────────────

    [Fact(DisplayName = "C2: the dm: id is the prefix + a fixed-length opaque base32 hash (no party id in the id)")]
    public void Id_Is_Prefix_Plus_FixedLength_Opaque_Hash()
    {
        var id = DmConversationId.Derive(Team, "alice", "bob");
        Assert.Equal(CommsConversation.DirectMessagePrefix.Length + DmConversationId.HashLength, id.Length);
        var hash = id[CommsConversation.DirectMessagePrefix.Length..];
        Assert.Equal(DmConversationId.HashLength, hash.Length);
        // Opaque: the party ids do NOT appear in the id (the hash hides who).
        Assert.DoesNotContain("alice", id, StringComparison.Ordinal);
        Assert.DoesNotContain("bob", id, StringComparison.Ordinal);
        // base32 lowercase alphanumeric body.
        Assert.Matches("^[0-9a-z]+$", hash);
    }

    // ── CONCAT-AMBIGUITY RESISTANCE: ("ab","c") and ("a","bc") must NOT collide (the NUL separator). ─────────

    [Fact(DisplayName = "C2: concat-ambiguous pairs do NOT collide (the NUL separator)")]
    public void Concat_Ambiguous_Pairs_Do_Not_Collide()
    {
        var ab_c = DmConversationId.Derive(Team, "ab", "c");
        var a_bc = DmConversationId.Derive(Team, "a", "bc");
        Assert.NotEqual(ab_c, a_bc);
    }

    // ── SELF-DM + bad input rejection. ──────────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "C2: a self-DM (both parties equal) is rejected — a 1:1 needs two distinct participants")]
    public void Self_Dm_Is_Rejected()
    {
        Assert.Throws<ArgumentException>(() => DmConversationId.Derive(Team, "alice", "alice"));
    }

    [Theory(DisplayName = "C2: null/empty inputs are rejected")]
    [InlineData(null, "alice", "bob")]
    [InlineData("", "alice", "bob")]
    [InlineData("team", null, "bob")]
    [InlineData("team", "alice", "")]
    public void Bad_Inputs_Are_Rejected(string? team, string? a, string? b)
    {
        // ThrowIfNullOrWhiteSpace throws ArgumentNullException (a subclass of ArgumentException) for null and
        // ArgumentException for empty/whitespace — ThrowsAny accepts either.
        Assert.ThrowsAny<ArgumentException>(() => DmConversationId.Derive(team!, a!, b!));
    }
}
