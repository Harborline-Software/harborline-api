using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.LocalNodeHost.Tests.Authorization;

/// <summary>
/// The gate's two argument refusals, stated as behaviour. <c>DecideProspectiveAdministratorAsync</c> answers
/// only <c>members:manage</c> on <c>members/handover</c>, and every entry point refuses a malformed act or
/// target before it reads a grant. Each case below goes green only because its guard runs: the scoped
/// Stryker run of <c>AuthorizationGate.cs</c> (lines 83-85 and the <c>Validate(request)</c> call in
/// <c>DecideCoreAsync</c>) names these tests as the killers.
/// </summary>
public sealed class GateEntryPointRefusalTests
{
    private static readonly TenantId Tenant = new("tenant");

    [Theory]
    // Wrong act on the handover target: members:admit is a real members act, but not the handover act.
    [InlineData("members:admit", "members", "handover")]
    // The handover act on any other members record.
    [InlineData("members:manage", "members", "invite")]
    // The handover act aimed at a record that is not a members record.
    [InlineData("members:manage", "record", "handover")]
    public async Task The_prospective_administrator_entry_point_refuses_anything_but_the_members_handover_act(
        string operation, string recordKind, string recordId)
    {
        var reads = 0;
        // A stranger with no roster edge: exactly the principal the prospective atoms would allow.
        var gate = TestAuthorization.GateWithRoster(
            _ => true, new AuthorizationRosterInputs("successor", Member: false, Ejected: false),
            observed: _ => reads++);
        var request = TestAuthorization.Write(Tenant, "successor")
            .Request(AuthorizationOperation.Parse(operation), recordKind, recordId);

        var error = await Assert.ThrowsAsync<ArgumentException>(
            () => gate.DecideProspectiveAdministratorAsync(request).AsTask());

        Assert.Equal("request", error.ParamName);
        Assert.StartsWith(
            "Prospective Administrator authority is valid only for the members handover act.", error.Message);
        Assert.Equal(0, reads);
    }

    /// <summary>The refusal above is narrow: the one act it admits reaches the gate and is decided.</summary>
    [Fact]
    public async Task The_prospective_administrator_entry_point_decides_the_members_handover_act()
    {
        var reads = 0;
        var gate = TestAuthorization.GateWithRoster(
            _ => true, new AuthorizationRosterInputs("successor", Member: false, Ejected: false),
            observed: _ => reads++);
        var request = TestAuthorization.Write(Tenant, "successor")
            .Request(AuthorizationOperation.Parse(TeamRolePermissions.MembersManage), "members", "handover");

        var decision = await gate.DecideProspectiveAdministratorAsync(request);

        Assert.Equal(AuthorizationVerdict.Allowed, decision.Verdict);
        Assert.NotEqual(0, reads);
    }

    public static TheoryData<string, AuthorizationGateRequest> MalformedRequests()
    {
        var at = DateTimeOffset.Parse("2026-09-29T12:00:00Z");
        var principal = new ActorId("party");
        AuthorizationGateRequest Request(string atom, string kind, string id, string scope) =>
            new(PermissionAtom.Parse(atom), principal, Tenant,
                new AuthorizationTarget(kind, id, ScopeExpression.Parse(scope)), at);

        return new()
        {
            // The act and target name the record at a scope that is not its canonical /records/{id}.
            { "The requested act and target must use the canonical target scope.",
                Request("records:read@/records", "record", "r-1", "/records") },
            // An ancestor act over a correctly scoped target: the act scope must be canonical too.
            { "The requested act and target must use the canonical target scope.",
                Request("records:read@/", "record", "r-1", "/records/r-1") },
            // A ledger act aimed at a plain record.
            { "The requested operation does not belong to the target record kind.",
                Request("ledger:post@/records/r-1", "record", "r-1", "/records/r-1") },
            // A tenant target naming a tenant other than the request's.
            { "A tenant target id must identify the request tenant.",
                Request("tenant:read@/", "tenant", "other-tenant", "/") },
        };
    }

    [Theory]
    [MemberData(nameof(MalformedRequests))]
    public async Task The_gate_refuses_a_malformed_act_or_target_before_reading_any_grant(
        string refusal, AuthorizationGateRequest request)
    {
        var reads = 0;
        // Every read allows, so only the shape check can refuse.
        var gate = TestAuthorization.GateWithRoster(
            _ => true, new AuthorizationRosterInputs("party", Member: true, Ejected: false),
            observed: _ => reads++);

        var error = await Assert.ThrowsAsync<ArgumentException>(() => gate.DecideAsync(request).AsTask());

        Assert.StartsWith(refusal, error.Message);
        Assert.Equal(0, reads);
    }
}
