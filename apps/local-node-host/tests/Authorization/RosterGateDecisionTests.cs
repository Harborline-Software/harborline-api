using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.LocalNodeHost.Tests.Authorization;

public sealed class RosterGateDecisionTests
{
    [Theory]
    [InlineData(true, false, true, true, false, true)]
    [InlineData(true, true, true, true, false, false)]
    [InlineData(true, false, false, true, false, false)]
    [InlineData(false, false, true, true, false, false)]
    [InlineData(true, false, true, false, false, false)]
    [InlineData(true, false, true, true, true, false)]
    public async Task Membership_Ejection_GrantAndDelegation_AreOneEvidencedDecision(
        bool member, bool ejected, bool holdsManage, bool grantAllowed, bool expands, bool allowed)
    {
        var permissions = holdsManage ? PermissionSet.Of("members:manage", "records:read") : PermissionSet.Of("records:read");
        var input = new AuthorizationRosterInputs("party", member, ejected, permissions)
        {
            RequireMember = true, RequireGrantCoverage = true,
            RequiredPermissions = PermissionSet.Of(expands ? "records:write" : "records:read")
        };
        var request = TestAuthorization.Write(new TenantId("tenant"))
            .Request(AuthorizationOperation.Parse("members:manage"), "members", "invite") with { Roster = input };
        var decision = await TestAuthorization.Gate(grantAllowed).DecideAsync(request);
        Assert.Equal(allowed, decision.Verdict == AuthorizationVerdict.Allowed);
        Assert.Same(input, decision.Evidence.Roster);
        Assert.Contains(decision.Evidence.Project()[1].Facts, fact => fact.Contains($"ejected:{ejected}"));
        Assert.Equal(AuthorizationCounterfactualKind.None, AuthorizationCounterfactual.From(decision.Evidence).Kind);
    }

    /// <summary>
    /// Ticket 294 slice 2a §1.4 — the prospective-Administrator rule, stated directly. A successor with NO
    /// roster edge is decided on the atoms the Administrator role is about to confer; a successor WITH a
    /// roster edge is decided on its own conferred grants, and the prospective atoms are not added. That
    /// single discriminator is what separates a handover to a stranger from a handover to a narrowed member.
    /// </summary>
    [Theory]
    // no roster edge, holds nothing → the prospective Administrator atoms decide → allowed.
    [InlineData(false, false, false, true)]
    // a roster edge that does not carry members:manage → own grants decide → refused.
    [InlineData(true, false, false, false)]
    // a roster edge that DOES carry members:manage → own grants decide → allowed.
    [InlineData(true, true, false, true)]
    // ejection outranks a prospect: no edge, but ejected → empty → refused.
    [InlineData(false, false, true, false)]
    public async Task A_prospective_successor_without_a_roster_edge_is_allowed_and_one_with_a_narrowed_edge_is_refused(
        bool member, bool holdsManage, bool ejected, bool allowed)
    {
        var input = new AuthorizationRosterInputs(
            "successor",
            member,
            ejected,
            holdsManage ? PermissionSet.Of("members:manage", "records:read") : PermissionSet.Of("records:read"))
        {
            ProspectiveAdministratorGrant = true,
        };
        var request = TestAuthorization.Write(new TenantId("tenant"), "successor")
            .Request(AuthorizationOperation.Parse("members:manage"), "members", "handover") with { Roster = input };

        var decision = await TestAuthorization.Gate(false).DecideAsync(request);

        Assert.Equal(allowed, decision.Verdict == AuthorizationVerdict.Allowed);
        Assert.Contains(
            decision.Evidence.Project()[1].Facts,
            fact => fact.Contains("prospective-administrator-grant:True"));
    }

    /// <summary>Without the flag the successor is decided on its own set alone — the flag is the whole rule.</summary>
    [Fact]
    public async Task Without_the_prospective_flag_a_successor_with_no_edge_and_no_grants_is_refused()
    {
        var input = new AuthorizationRosterInputs("successor", false, false, PermissionSet.Of("records:read"));
        var request = TestAuthorization.Write(new TenantId("tenant"), "successor")
            .Request(AuthorizationOperation.Parse("members:manage"), "members", "handover") with { Roster = input };

        var decision = await TestAuthorization.Gate(false).DecideAsync(request);

        Assert.Equal(AuthorizationVerdict.Denied, decision.Verdict);
    }
}
