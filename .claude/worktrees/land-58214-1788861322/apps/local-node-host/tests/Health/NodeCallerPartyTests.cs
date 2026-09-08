using Microsoft.AspNetCore.Http;

using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.LocalNodeHost.Data.Audit;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Health;

namespace Harborline.Api.LocalNodeHost.Tests.Health;

/// <summary>
/// The real-actor accessor (MTW-2 2612-A): a bound selected-session principal stamps its own
/// canonical Party; an unbound request falls back to the single operator party.
/// </summary>
[Trait("PlanCard", "MTW-2-2839")]
public sealed class NodeCallerPartyTests
{
    [Fact]
    public void Two_Distinct_Bound_Members_Stamp_Two_Distinct_Parties()
    {
        var alice = RequestBoundTo("party:alice");
        var bob = RequestBoundTo("party:bob");

        var aliceParty = NodeCallerParty.Resolve(alice);
        var bobParty = NodeCallerParty.Resolve(bob);

        Assert.Equal("party:alice", aliceParty.Value);
        Assert.Equal("party:bob", bobParty.Value);
        Assert.NotEqual(aliceParty, bobParty);
        // Neither real member collapses to the operator fallback.
        Assert.NotEqual(NodeCallerParty.OperatorParty, aliceParty);
        Assert.NotEqual(NodeCallerParty.OperatorParty, bobParty);
    }

    [Fact]
    public void Unbound_Request_Falls_Back_To_The_Operator_Party()
    {
        // The single-operator bootstrap/desktop path: no selected-session principal is bound.
        // This test is what keeps the refusal below honest — a blanket refusal would break the
        // desktop plane (bootstrap, hosted services, detached workflow effects), so the fallback
        // MUST survive. Assert the plane signal too: the desktop path is "no web principal bound",
        // not merely "no request feature set".
        var http = new DefaultHttpContext();

        Assert.False(NodeCallerAttributionScope.HasBoundWebPrincipal);

        var party = NodeCallerParty.Resolve(http);

        Assert.Equal(NodeCallerParty.OperatorParty, party);
        Assert.Equal(ActiveTeamAuthorizationContext.LocalUserId, party.Value);
    }

    [Fact]
    public void Web_Plane_Request_Missing_Its_Bound_Principal_Is_Refused_Not_Attributed_To_The_Operator()
    {
        // A selected-session request: the listener opened the attribution scope, so a web-plane
        // member IS acting. The request feature is deliberately absent — the bug this card exists
        // for. Falling back here stamps the operator on a member's mutation, indistinguishably from
        // a legitimate desktop call, and nothing reports it.
        var http = new DefaultHttpContext();
        using var scope = NodeCallerAttributionScope.Enter(AttributionFor("party:alice"));

        Assert.True(NodeCallerAttributionScope.HasBoundWebPrincipal);
        Assert.Null(http.Features.Get<SelectedSessionRequestPrincipal>());

        var refusal = Assert.Throws<NodeCallerAttributionRefusedException>(
            () => NodeCallerParty.Resolve(http));

        // The refusal must not silently carry the operator identity forward in any form.
        Assert.DoesNotContain(
            NodeCallerParty.OperatorParty.Value,
            refusal.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Web_Plane_Request_With_Its_Bound_Principal_Still_Stamps_The_Member()
    {
        // The unchanged happy path, driven with the plane signal ALSO set — otherwise the refusal
        // could pass for the wrong reason (refusing every scope-open request).
        var http = RequestBoundTo("party:alice");
        using var scope = NodeCallerAttributionScope.Enter(AttributionFor("party:alice"));

        var party = NodeCallerParty.Resolve(http);

        Assert.Equal("party:alice", party.Value);
        Assert.NotEqual(NodeCallerParty.OperatorParty, party);
    }

    private static NodeCallerAttribution AttributionFor(string canonicalParty) => new(
        MemberPartyId: canonicalParty,
        MembershipId: "membership-" + canonicalParty,
        MembershipOwnerVersion: 1,
        SessionCorrelationId: "session-" + canonicalParty,
        CoordinationCorrelationId: "coordination-" + canonicalParty,
        AuthorizationEpoch: 1);

    private static HttpContext RequestBoundTo(string canonicalParty)
    {
        var http = new DefaultHttpContext();
        http.Features.Set(new SelectedSessionRequestPrincipal(
            accountId: "account-" + canonicalParty,
            tenantId: new TenantId("tenant-a"),
            principalUserId: new PrincipalUserId("principal-" + canonicalParty),
            canonicalParty: new CanonicalPartyReference(canonicalParty),
            membershipId: "membership-" + canonicalParty,
            membershipOwnerVersion: 1,
            pinnedGrantOwnerVersions: [new PinnedGrantOwnerVersion("grant-" + canonicalParty, 1)],
            authorizationEpoch: 1,
            sessionCorrelationId: "session-" + canonicalParty,
            coordinationCorrelationId: "coordination-" + canonicalParty));
        return http;
    }
}
