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
        var input = new AuthorizationRosterInputs("party", member, ejected)
        {
            RequireMember = true, RequireGrantCoverage = true,
            RequiredPermissions = PermissionSet.Of(expands ? "records:write" : "members:manage")
        };
        var request = TestAuthorization.Write(new TenantId("tenant"))
            .Request(AuthorizationOperation.Parse("members:manage"), "members", "invite") with { Roster = input };
        var decision = await TestAuthorization.Gate(grantAllowed && holdsManage).DecideAsync(request);
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
        // Ticket 293 slice 4 — the roster no longer carries a permission set, so "holds members:manage"
        // is what the successor's OWN conferred grants derive at the gate's closure, not a caller input.
        var input = new AuthorizationRosterInputs("successor", member, ejected)
        {
            ProspectiveAdministratorGrant = true,
        };
        var request = TestAuthorization.Write(new TenantId("tenant"), "successor")
            .Request(AuthorizationOperation.Parse("members:manage"), "members", "handover") with { Roster = input };

        var decision = await TestAuthorization.Gate(holdsManage).DecideAsync(request);

        Assert.Equal(allowed, decision.Verdict == AuthorizationVerdict.Allowed);
        Assert.Contains(
            decision.Evidence.Project()[1].Facts,
            fact => fact.Contains("prospective-administrator-grant:True"));
    }

    /// <summary>Without the flag the successor is decided on its own set alone — the flag is the whole rule.</summary>
    [Fact]
    public async Task Without_the_prospective_flag_a_successor_with_no_edge_and_no_grants_is_refused()
    {
        var input = new AuthorizationRosterInputs("successor", false, false);
        var request = TestAuthorization.Write(new TenantId("tenant"), "successor")
            .Request(AuthorizationOperation.Parse("members:manage"), "members", "handover") with { Roster = input };

        var decision = await TestAuthorization.Gate(false).DecideAsync(request);

        Assert.Equal(AuthorizationVerdict.Denied, decision.Verdict);
    }

    /// <summary>
    /// Ticket 293 slice 4 fix 4, item 7 — names the behaviour change the review found unpinned. The roster
    /// branch no longer PROJECTS its deciding set to the install root: a roster-carrying request at a narrower
    /// target scope is decided on the derivations the gate read for THAT scope. Before this slice the branch
    /// replaced the atoms with the caller's roster set re-parsed at <c>@/</c>, so a derivation that existed only
    /// at the record scope could not decide anything.
    /// </summary>
    [Fact]
    public async Task A_roster_carrying_request_at_a_narrower_scope_decides_on_that_scopes_derivations()
    {
        var input = new AuthorizationRosterInputs("party", true, false)
        {
            RequireMember = true, RequireGrantCoverage = true,
        };
        var request = TestAuthorization.Write(new TenantId("tenant"))
            .Request(AuthorizationOperation.Parse("records:read"), "record", "r1") with { Roster = input };

        var decision = await TestAuthorization.Gate(true).DecideAsync(request);

        Assert.Equal(AuthorizationVerdict.Allowed, decision.Verdict);
        // The deciding derivation sits at the RECORD scope, not at the install root.
        Assert.Equal("/records/r1", Assert.Single(decision.Evidence.Bindings).Atom.Scope.Value);
        Assert.DoesNotContain(decision.Evidence.Bindings, binding => binding.Atom.Scope.Value == "/");
    }
}
