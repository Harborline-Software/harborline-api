using Microsoft.EntityFrameworkCore;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Runtime;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Data.Search;
using Harborline.Api.LocalNodeHost.Tests.Authorization;

using NSubstitute;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

/// <summary>
/// ck-10 S4 (DES-0029, ADR 0038): an admitted admin grant revocation runs the six stages through
/// <see cref="WritePipeline.RunAsync"/> under the decision the caller carried. Each refusal stops at its own stage
/// and leaves the grant in force.
/// </summary>
public sealed class GrantRevocationWritePipelineTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TenantId Tenant = new("grant-pipeline");
    private static readonly ActorId Admin = new("grant-admin");

    // ADR 0038's order, written out here rather than read from WritePipeline.Order.
    private static readonly WritePipelineStage[] SixStages =
    [
        WritePipelineStage.Authorize, WritePipelineStage.Bind, WritePipelineStage.Mutate,
        WritePipelineStage.Validate, WritePipelineStage.Commit, WritePipelineStage.React,
    ];

    [Fact(DisplayName = "ck-10 S4: a revocation runs the six stages and stores the evidence it was given")]
    public async Task Revoke_RunsTheSixStages()
    {
        var h = await Harness.CreateAsync();

        var revoked = await h.Writer.RevokeAsync(Tenant, h.Grant, Evidence(Admin, At), Decision(h.Grant));

        Assert.Equal(SixStages, h.Stages);
        var stored = await h.Grants.FindAsync(Tenant, h.Grant);
        Assert.Equal(GrantStatus.Revoked, stored!.Status);
        Assert.Equal(Evidence(Admin, At), stored.Revocation);
        Assert.Equal(stored, revoked);
    }

    [Fact(DisplayName = "ck-10 S4: a decision for another grant stops at authorize, before the grant is read")]
    public async Task Revoke_DecisionForAnotherGrant_StopsAtAuthorize()
    {
        var h = await Harness.CreateAsync();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            h.Writer.RevokeAsync(Tenant, h.Grant, Evidence(Admin, At), Decision(GrantId.New())));

        Assert.Equal([WritePipelineStage.Authorize], h.Stages);
        Assert.Equal(GrantStatus.Active, (await h.Grants.FindAsync(Tenant, h.Grant))!.Status);
    }

    [Fact(DisplayName = "ck-10 S4: a grant that does not exist settles at bind with nothing written")]
    public async Task Revoke_MissingGrant_SettlesAtBind()
    {
        var h = await Harness.CreateAsync();
        var missing = GrantId.New();

        Assert.Null(await h.Writer.RevokeAsync(Tenant, missing, Evidence(Admin, At), Decision(missing)));

        Assert.Equal(WritePipelineStage.Bind, h.Stages[^1]);
        Assert.Null(await h.Grants.FindAsync(Tenant, missing));
    }

    [Fact(DisplayName = "ck-10 S4: back-dated evidence is refused at validate; the grant stays in force and a correct retry revokes it")]
    public async Task Revoke_BackDatedEvidence_IsRefusedAtValidate_AndARetrySucceeds()
    {
        var h = await Harness.CreateAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => h.Writer.RevokeAsync(
            Tenant, h.Grant, Evidence(Admin, new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero)), Decision(h.Grant)));
        Assert.Equal(WritePipelineStage.Validate, h.Stages[^1]);
        Assert.Equal(GrantStatus.Active, (await h.Grants.FindAsync(Tenant, h.Grant))!.Status);

        h.Stages.Clear();
        await h.Writer.RevokeAsync(Tenant, h.Grant, Evidence(Admin, At), Decision(h.Grant));
        Assert.Equal(SixStages, h.Stages);
        Assert.Equal(GrantStatus.Revoked, (await h.Grants.FindAsync(Tenant, h.Grant))!.Status);
    }

    [Fact(DisplayName = "ck-10 S4: evidence attributed to someone other than the decided principal is refused at validate")]
    public async Task Revoke_EvidenceForAnotherActor_IsRefusedAtValidate()
    {
        var h = await Harness.CreateAsync();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            h.Writer.RevokeAsync(Tenant, h.Grant, Evidence(new ActorId("someone-else"), At), Decision(h.Grant)));

        Assert.Equal(WritePipelineStage.Validate, h.Stages[^1]);
        Assert.Equal(GrantStatus.Active, (await h.Grants.FindAsync(Tenant, h.Grant))!.Status);
    }

    private static GrantRevocation Evidence(ActorId by, DateTimeOffset at) =>
        new(by, at, new GrantReason(GrantReasonCodes.RevocationOffboarding, "pipeline"));

    private static AuthorizationDecision Decision(GrantId grant) => TestAuthorization.AllowedDecision(
        Tenant, grant.ToString(), "members", TeamRolePermissions.MembersManage, Admin.Value, At);

    private sealed class Harness : IWritePipelineObserver
    {
        private Harness(InMemoryGrantStore grants, GrantId grant)
        {
            Grants = grants;
            Grant = grant;
            Writer = new AuthorizedGrantRevocationWriter(
                grants, Substitute.For<IDbContextFactory<NodeLocalSearchDbContext>>(), this);
        }

        public InMemoryGrantStore Grants { get; }
        public GrantId Grant { get; }
        public AuthorizedGrantRevocationWriter Writer { get; }
        public List<WritePipelineStage> Stages { get; } = [];

        public void OnStage(WritePipelineStage stage) => Stages.Add(stage);

        public static async Task<Harness> CreateAsync()
        {
            var (grants, _) = TestInMemoryAuthorizationStores.Pair();
            var member = new ActorId("grant-member");
            var granted = await grants.AppendAsync(Tenant, new AccessGrant(
                GrantId.New(), Tenant, member, new RoleReference(RoleVocabularies.Domain, "member"), ScopeExpression.Parse("/"),
                GrantResidency.Cache, new GrantValidity(At.AddDays(-1), At.AddDays(30)),
                GranterKind.Person, Admin, At.AddDays(-1),
                new GrantProvenance(GrantSourceKind.Manual, new GrantReason(GrantReasonCodes.Manual), Admin),
                At.AddDays(-1)));
            return new Harness(grants, granted.GrantId);
        }
    }
}
