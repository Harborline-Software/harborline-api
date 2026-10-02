using Microsoft.EntityFrameworkCore;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Data.Search;
using Harborline.Api.LocalNodeHost.Tests.Authorization;

using NSubstitute;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

/// <summary>
/// The grant writer's members other than revocation check the decision the caller carried before they reach the
/// grant store. A decision for another grant is refused and the store is never called; the matching decision reaches
/// it exactly once. Mutation runs on T-519 found that deleting these checks failed no test.
/// </summary>
public sealed class GrantWriterCarriedDecisionTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TenantId Tenant = new("grant-carried-decision");
    private static readonly ActorId Admin = new("grant-admin");
    private static readonly GrantId Current = GrantId.New();
    private static readonly GrantId Other = GrantId.New();

    [Fact(DisplayName = "Grant writer: a handover under a decision for another grant is refused before the store")]
    public async Task Handover_DecisionForAnotherGrant_IsRefusedBeforeTheStore()
    {
        var h = new Harness();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            h.Writer.HandoverAsync(Tenant, Current, Successor(), Evidence(), Decision(Other)));

        await h.Grants.DidNotReceiveWithAnyArgs().HandoverAdministratorAsync(default, default, default!, default!, default);
    }

    [Fact(DisplayName = "Grant writer: a handover under the matching decision reaches the store once")]
    public async Task Handover_MatchingDecision_ReachesTheStoreOnce()
    {
        var h = new Harness();
        var successor = Successor();

        await h.Writer.HandoverAsync(Tenant, Current, successor, Evidence(), Decision(Current));

        await h.Grants.Received(1).HandoverAdministratorAsync(Tenant, Current, successor, Evidence(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Grant writer: an admission narrowing under a decision for another grant is refused before any read")]
    public async Task Narrow_DecisionForAnotherGrant_IsRefusedBeforeAnyRead()
    {
        var h = new Harness();

        await Assert.ThrowsAsync<ArgumentException>(() => h.Writer.NarrowAsync(
            Tenant, Current, PermissionSet.Of(TeamRolePermissions.RecordsWrite), Evidence(), Guid.NewGuid(), Decision(Other)));

        await h.Factory.DidNotReceiveWithAnyArgs().CreateDbContextAsync(default);
        h.Factory.DidNotReceiveWithAnyArgs().CreateDbContext();
    }

    [Fact(DisplayName = "Grant writer: a scope narrowing under a decision for another grant is refused before the store")]
    public async Task NarrowScope_DecisionForAnotherGrant_IsRefusedBeforeTheStore()
    {
        var h = new Harness();

        await Assert.ThrowsAsync<ArgumentException>(() => h.Writer.NarrowScopeAsync(
            Tenant, Current, ScopeExpression.Parse("/records"), GrantId.New(), Evidence(), Decision(Other)));

        await h.Grants.DidNotReceiveWithAnyArgs().NarrowScopeAsync(default, default, default!, default, default!, default);
    }

    [Fact(DisplayName = "Grant writer: a scope narrowing under the matching decision reaches the store once")]
    public async Task NarrowScope_MatchingDecision_ReachesTheStoreOnce()
    {
        var h = new Harness();
        var narrowed = ScopeExpression.Parse("/records");
        var successor = GrantId.New();

        await h.Writer.NarrowScopeAsync(Tenant, Current, narrowed, successor, Evidence(), Decision(Current));

        await h.Grants.Received(1).NarrowScopeAsync(Tenant, Current, narrowed, successor, Evidence(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Grant writer: a review under a decision for another grant is refused before the store")]
    public async Task Review_DecisionForAnotherGrant_IsRefusedBeforeTheStore()
    {
        var h = new Harness();

        await Assert.ThrowsAsync<ArgumentException>(() => h.Writer.RecordReviewAsync(Tenant, Current, At, Admin, Decision(Other)));

        await h.Grants.DidNotReceiveWithAnyArgs().RecordReviewAsync(default, default, default, default!, default);
    }

    [Theory(DisplayName = "Grant writer: a review attributed to another actor or instant than the decision is refused before the store")]
    [InlineData("someone-else", 0)]
    [InlineData("grant-admin", 1)]
    public async Task Review_AttributionNotMatchingTheDecision_IsRefusedBeforeTheStore(string reviewer, int offsetMinutes)
    {
        var h = new Harness();

        var refused = await Assert.ThrowsAsync<ArgumentException>(() => h.Writer.RecordReviewAsync(
            Tenant, Current, At.AddMinutes(offsetMinutes), new ActorId(reviewer), Decision(Current)));

        Assert.Equal("admittedDecision", refused.ParamName);
        await h.Grants.DidNotReceiveWithAnyArgs().RecordReviewAsync(default, default, default, default!, default);
    }

    [Fact(DisplayName = "Grant writer: a review attributed to the decision's actor and instant reaches the store once")]
    public async Task Review_MatchingDecision_ReachesTheStoreOnce()
    {
        var h = new Harness();

        await h.Writer.RecordReviewAsync(Tenant, Current, At, Admin, Decision(Current));

        await h.Grants.Received(1).RecordReviewAsync(Tenant, Current, At, Admin, Arg.Any<CancellationToken>());
    }

    private static GrantRevocation Evidence() =>
        new(Admin, At, new GrantReason(GrantReasonCodes.RevocationOffboarding, "carried-decision"));

    private static AccessGrant Successor() => new(
        GrantId.New(), Tenant, new ActorId("grant-successor"), new RoleReference(RoleVocabularies.Domain, "member"),
        ScopeExpression.Parse("/"), GrantResidency.Cache, new GrantValidity(At, At.AddDays(30)),
        GranterKind.Person, Admin, At, new GrantProvenance(GrantSourceKind.Manual, new GrantReason(GrantReasonCodes.Manual), Admin),
        At);

    private static AuthorizationDecision Decision(GrantId grant) => TestAuthorization.AllowedDecision(
        Tenant, grant.ToString(), "members", TeamRolePermissions.MembersManage, Admin.Value, At);

    private sealed class Harness
    {
        public Harness() => Writer = new AuthorizedGrantRevocationWriter(Grants, Factory);

        public IGrantStore Grants { get; } = Substitute.For<IGrantStore>();
        public IDbContextFactory<NodeLocalSearchDbContext> Factory { get; } =
            Substitute.For<IDbContextFactory<NodeLocalSearchDbContext>>();
        public AuthorizedGrantRevocationWriter Writer { get; }
    }
}
