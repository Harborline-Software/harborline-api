using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

public sealed class InitialGrantIssuanceServiceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-01T12:00:00Z");

    [Fact]
    public async Task AdmissionIssuesRoleAndProvenanceWithoutPermissionPowers()
    {
        var store = TestInMemoryAuthorizationStores.GrantStore();
        var grant = await new InitialGrantIssuanceService(
            store, TestAuthorization.AllowGate(), new FixedTimeProvider(Now)).IssueAsync(Admission());
        Assert.Equal(AccessGrantAuthorizationSeed.MemberRole, grant.Role);
        Assert.Equal("/", grant.Scope.Value);
        Assert.Equal(GranterKind.Person, grant.GranterKind);
        Assert.Equal(GrantSourceKind.Invitation, grant.Grant.Source);
        Assert.Equal(GrantReasonCodes.Invitation, grant.Grant.Reason.Code);
    }

    [Fact]
    public async Task AdmissionReplayIsSourceReferenceIdempotent()
    {
        var store = TestInMemoryAuthorizationStores.GrantStore();
        var service = new InitialGrantIssuanceService(
            store, TestAuthorization.AllowGate(), new FixedTimeProvider(Now));
        var first = await service.IssueAsync(Admission());
        var replay = await service.IssueAsync(Admission());
        Assert.Equal(first, replay);
        Assert.Single(await store.SnapshotAsync(Admission().TenantId));
    }

    [Fact]
    public async Task AdmissionReturnsActualPostWriteAuthorizationEpoch()
    {
        var admission = Admission();
        var store = TestInMemoryAuthorizationStores.GrantStore();
        await store.AppendAsync(admission.TenantId, new AccessGrant(
            GrantId.New(), admission.TenantId, new ActorId(admission.CanonicalTenantPrincipal.Value),
            admission.Role, ScopeExpression.Parse("/records/prior"), GrantResidency.Cache,
            new GrantValidity(Now.AddDays(-1)), GranterKind.Person, new ActorId("inviter"), Now.AddDays(-1),
            admission.Grant, Now.AddDays(-1)), "prior");

        var result = await new InitialGrantIssuanceService(
                store, TestAuthorization.AllowGate(), new FixedTimeProvider(Now))
            .IssueWithEpochAsync(admission);

        Assert.Equal(2, result.AuthorizationEpoch);
    }

    [Fact]
    public void AdmissionContractCarriesRoleAndGrantProvenanceOnly()
    {
        var properties = typeof(AdmissionCompleted).GetProperties().Select(p => p.PropertyType).ToArray();
        Assert.Contains(typeof(RoleReference), properties);
        Assert.Contains(typeof(GrantProvenance), properties);
        Assert.DoesNotContain(typeof(PermissionSet), properties);
    }

    private static AdmissionCompleted Admission() => new(
        TenantId.FromString("tenant-a"), new PrincipalUserId("joiner"),
        new CanonicalPartyReference("party-a"), new PrincipalUserId("inviter"), "invite-a",
        AccessGrantAuthorizationSeed.MemberRole,
        new GrantProvenance(GrantSourceKind.Invitation,
            new GrantReason(GrantReasonCodes.Invitation, "invite-a"), new ActorId("inviter")));

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
