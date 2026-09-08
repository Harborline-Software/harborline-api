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
}
