using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

/// <summary>
/// Ticket 293 slice 5 — the roster's whole contribution to authorization, enumerated: two booleans.
/// <para>
/// After slice 4 the gate decides from grants and the signed roster edge only CONSTRAINS, so this helper
/// holds no permission set and cannot. These rows pin both axes (membership and ejection) in both
/// directions, and the shape assertion pins that nothing set-shaped comes back — which is what keeps a
/// later "just return the permissions too" edit from re-opening the read the M1 exit test forbids.
/// </para>
/// </summary>
public sealed class EffectiveMemberPermissionsTests
{
    private static readonly Guid Team = Guid.Parse("29350000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch.AddDays(1);
    private const string Founder = "party-founder";
    private const string Member = "party-member";
    private static readonly Ed25519Verifier Verifier = new();

    [Fact(DisplayName = "293 s5: a live member reads member=true, ejected=false")]
    public void A_live_member_is_a_member_and_not_ejected()
    {
        var (roster, _) = RosterWithMember();

        var inputs = EffectiveMemberPermissions.Read(roster, Member, new ActorId(Member));

        Assert.Equal(Member, inputs.PartyId);
        Assert.True(inputs.Member);
        Assert.False(inputs.Ejected);
    }

    [Fact(DisplayName = "293 s5: a party with no edge reads member=false, ejected=false")]
    public void A_party_with_no_edge_is_neither_member_nor_ejected()
    {
        var (roster, _) = RosterWithMember();

        // The deferred-admission population: an authenticated web invitee with a live grant and no signed
        // edge. Not a member, but NOT ejected either — the difference the gate needs to let the grant stand.
        var inputs = EffectiveMemberPermissions.Read(roster, "party-never-admitted", new ActorId("party-never-admitted"));

        Assert.False(inputs.Member);
        Assert.False(inputs.Ejected);
    }

    [Fact(DisplayName = "293 s5: a revoked member reads member=false, ejected=true")]
    public void A_revoked_member_is_ejected()
    {
        var (roster, _) = RosterWithMember();
        var revoked = roster.Revoke(Founder, Member);

        var inputs = EffectiveMemberPermissions.Read(revoked, Member, new ActorId(Member));

        Assert.False(inputs.Member);
        Assert.True(inputs.Ejected);
    }

    [Fact(DisplayName = "293 s5: ejection is seen through the GATE principal as well as the canonical party")]
    public void Ejection_is_seen_through_the_gate_principal_too()
    {
        // During the identity migration either existing key can carry the signed removal, so a removal
        // signed against the principal the gate reads must eject the canonical party too.
        var (roster, _) = RosterWithMember();
        var revoked = roster.Revoke(Founder, Member);

        var inputs = EffectiveMemberPermissions.Read(revoked, "party-other-key", new ActorId(Member));

        Assert.False(inputs.Member);
        Assert.True(inputs.Ejected);
    }

    [Fact(DisplayName = "293 s5: the roster read produces no permission set, only membership and ejection")]
    public void The_roster_read_produces_no_permission_set()
    {
        var (roster, _) = RosterWithMember();
        var inputs = EffectiveMemberPermissions.Read(roster, Member, new ActorId(Member));

        // AuthorizationRosterInputs also carries the act's REQUIREMENTS (RequireMember, RequiredPermissions)
        // — what the gate must check, supplied by the deciding site. This read supplies NONE of them: a set
        // filled in here would be an authorization answer taken outside the gate, on data a replicated
        // member does not carry. The helper's entire output is the party id and two booleans.
        Assert.Empty(inputs.RequiredPermissions.Permissions);
        Assert.Null(inputs.RegistryMember);
        Assert.False(inputs.RequireMember);
        Assert.False(inputs.RequireGrantCoverage);
        Assert.False(inputs.ProspectiveAdministratorGrant);
        Assert.Equal(new AuthorizationRosterInputs(Member, Member: true, Ejected: false), inputs);
    }

    private static (MemberRoster Roster, IOperationSigner FounderSigner) RosterWithMember()
    {
        var founder = KeyPair.Generate();
        var member = KeyPair.Generate();
        var founderSigner = new Ed25519Signer(founder);
        var roster = MemberRoster
            .Genesis(Team, Founder, founderSigner, Verifier, At, Guid.NewGuid())
            .Admit(
                Founder, founderSigner, Member, member.PrincipalId,
                PermissionCompositions.Member, Verifier, At.AddMinutes(1), Guid.NewGuid());
        return (roster, founderSigner);
    }
}
