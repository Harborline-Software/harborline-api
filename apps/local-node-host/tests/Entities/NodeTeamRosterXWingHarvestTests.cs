using System.Security.Cryptography;

using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.LocalNodeHost.Data.Roster;
using Harborline.Api.LocalNodeHost.Enrollment;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// Live-harvest coverage for the X-Wing public-key map on <see cref="NodeTeamRoster"/> (PQC Phase 2 / BL-01
/// increment 2c-iii-b — the suite-#3 capability source PR-B's writer consumes). Proves
/// <see cref="NodeTeamRoster.XWingPublicKeyOf"/> surfaces a carried X-Wing key ONLY for a genesis-validated LIVE
/// member (UNSIGNED-by-association), exactly mirroring the transport/DM maps: a live member's carried key is
/// harvested; a non-member's is never harvested; a member dropped from the live set loses its key on rebuild.
/// </summary>
public sealed class NodeTeamRosterXWingHarvestTests
{
    private static readonly Guid Team = Guid.Parse("7e57dddd-0000-0000-0000-00000000000d");
    private static readonly IOperationVerifier Verifier = new Ed25519Verifier();

    private static byte[] FakeXWingKey() => RandomNumberGenerator.GetBytes(RosterRecordCrdtState.XWingPublicKeyLength);

    private static (MemberRoster roster, string founderParty, IOperationSigner founderSigner) FoundedRoster()
    {
        var founderKp = KeyPair.Generate();
        var founderSigner = new Ed25519Signer(founderKp);
        const string founder = "os:A#founder";
        var roster = MemberRoster.Genesis(Team, founder, founderSigner, Verifier, DateTimeOffset.UtcNow, Guid.NewGuid());
        return (roster, founder, founderSigner);
    }

    [Fact(DisplayName = "XWingPublicKeyOf surfaces a carried X-Wing key for a LIVE member (by-association)")]
    public void XWingPublicKeyOf_Surfaces_LiveMember_Key()
    {
        var (roster, founder, founderSigner) = FoundedRoster();
        var memberKp = KeyPair.Generate();
        const string member = "os:B#member";
        var withMember = roster.Admit(
            founder, founderSigner, member, memberKp.PrincipalId,
            PermissionSet.From(new[] { "members:admit" }), Verifier, DateTimeOffset.UtcNow, Guid.NewGuid());

        var node = new NodeTeamRoster(roster);
        var memberXWing = FakeXWingKey();
        // Adopt the synced roster carrying the member's X-Wing key (the projection passes keys for validated live
        // members only — here we pass it directly for the member party).
        node.AdoptSyncedRoster(
            withMember,
            transportKeysByConvergedMember: null,
            dmKeysByConvergedMember: null,
            xwingKeysByConvergedMember: new Dictionary<string, byte[]> { [member] = memberXWing });

        var resolved = node.XWingPublicKeyOf(member);
        Assert.NotNull(resolved);
        Assert.True(resolved!.AsSpan().SequenceEqual(memberXWing));
    }

    [Fact(DisplayName = "XWingPublicKeyOf returns null for a NON-MEMBER even if a key is carried (by-association drop)")]
    public void XWingPublicKeyOf_Null_For_NonMember()
    {
        var (roster, _, _) = FoundedRoster();
        var node = new NodeTeamRoster(roster);

        // Carry an X-Wing key for a party that is NOT a member of the synced roster — it must be filtered out (a
        // forged/orphan key for a non-member is never harvested into the live map; the writer can never box #3 to it).
        node.AdoptSyncedRoster(
            roster,
            xwingKeysByConvergedMember: new Dictionary<string, byte[]> { ["os:Z#stranger"] = FakeXWingKey() });

        Assert.Null(node.XWingPublicKeyOf("os:Z#stranger"));
    }

    [Fact(DisplayName = "XWingPublicKeyOf returns null for a member with NO carried X-Wing key (not X-Wing-capable)")]
    public void XWingPublicKeyOf_Null_When_No_Key_Carried()
    {
        var (roster, _, _) = FoundedRoster();
        var node = new NodeTeamRoster(roster);

        // No X-Wing keys carried at all → the founder is a live member but is NOT X-Wing-capable → null (the writer
        // boxes it suite #1, the safe degrade).
        node.AdoptSyncedRoster(roster);

        Assert.Null(node.XWingPublicKeyOf(roster.GenesisPartyId));
    }

    [Fact(DisplayName = "a member dropped from the live set loses its X-Wing key on the next rebuild (revocation-drop)")]
    public void XWingKey_Dropped_When_Member_Leaves_Live_Set()
    {
        var (roster, founder, founderSigner) = FoundedRoster();
        var memberKp = KeyPair.Generate();
        const string member = "os:B#member";
        // Grant only what the genesis founder holds (members:admit) — admitting a wider set would violate
        // no-escalation; the X-Wing harvest under test is independent of which permission the member holds.
        var withMember = roster.Admit(
            founder, founderSigner, member, memberKp.PrincipalId,
            PermissionSet.From(new[] { "members:admit" }), Verifier, DateTimeOffset.UtcNow, Guid.NewGuid());

        var node = new NodeTeamRoster(roster);
        node.AdoptSyncedRoster(
            withMember,
            xwingKeysByConvergedMember: new Dictionary<string, byte[]> { [member] = FakeXWingKey() });
        Assert.NotNull(node.XWingPublicKeyOf(member)); // present while a live member.

        // Re-adopt a roster WITHOUT the member as a live member (the genesis-only roster), carrying no X-Wing keys.
        // The rebuild is keyed on the live-member set, so the member's X-Wing key is dropped (revocation-drop-by-
        // rebuild — the same property the transport/DM maps have).
        node.AdoptSyncedRoster(roster);
        Assert.Null(node.XWingPublicKeyOf(member));
    }
}
