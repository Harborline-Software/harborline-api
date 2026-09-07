using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.LocalNodeHost.Data.Search.Vector;

namespace Harborline.Api.LocalNodeHost.Tests.Search.Vector;

public sealed class GrantWriterVersionFenceTests
{
    private static readonly TenantId Tenant = TenantId.FromString("tenant-a");
    private static readonly ActorId User = new("user-a");
    private static readonly ActorId Admin = new("admin-a");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-01T12:00:00Z");

    [Fact]
    public async Task GrantStore_AppendIsSourceReferenceIdempotentAndImmutable()
    {
        await using var h = await VecTestHarness.CreateAsync();
        var grant = Grant();
        var first = await h.GrantStore.AppendAsync(Tenant, grant, "source-a");
        var replay = await h.GrantStore.AppendAsync(Tenant, grant, "source-a");
        Assert.Equal(first, replay);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.GrantStore.AppendAsync(
            Tenant, grant, "source-b"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.GrantStore.AppendAsync(
            Tenant, Grant() with { GrantId = grant.GrantId, Subject = new ActorId("other") }, "source-a"));
    }

    [Fact]
    public async Task GrantStore_ValidityChangeRetainsEvidenceAndAdvancesVersion()
    {
        await using var h = await VecTestHarness.CreateAsync();
        var grant = await h.GrantStore.AppendAsync(Tenant, Grant(), "source-a");
        var changed = await h.GrantStore.ChangeValidityAsync(Tenant, grant.GrantId,
            new GrantValidity(Now, Now.AddDays(2)), Admin, new GrantReason(GrantReasonCodes.Manual));
        Assert.Equal(grant.Role, changed!.Role);
        Assert.Equal(grant.Grant, changed.Grant);
        Assert.Equal(Now.AddDays(2), changed.Validity.ValidTo);
        Assert.Equal(Admin, changed.ValidityChange!.ChangedBy);
        Assert.Equal(GrantReasonCodes.Manual, changed.ValidityChange.Reason.Code);
        await using var db = h.Store.CreateContext();
        var row = db.Grants.Single();
        Assert.Equal(2, row.OwnerVersion);
        Assert.Equal(Admin.Value, row.ValidityChangedBy);
        Assert.Equal(GrantReasonCodes.Manual, row.ValidityChangeReasonCode);
    }

    [Fact]
    public async Task GrantStore_ReviewRetainsRowAndAdvancesReviewTimestamp()
    {
        await using var h = await VecTestHarness.CreateAsync();
        var grant = await h.GrantStore.AppendAsync(Tenant, Grant(), "source-a");
        var reviewed = await h.GrantStore.RecordReviewAsync(Tenant, grant.GrantId, Now.AddHours(1), Admin);
        Assert.Equal(Now.AddHours(1), reviewed!.LastReviewedAt);
        Assert.Equal(Admin, reviewed.LastReviewedBy);
        var roundTripped = await h.GrantStore.FindAsync(Tenant, grant.GrantId);
        Assert.Equal(Admin, roundTripped!.LastReviewedBy);
    }

    [Fact]
    public async Task GrantStore_RevocationRetainsRowRevokerAndReason()
    {
        await using var h = await VecTestHarness.CreateAsync();
        var grant = await h.GrantStore.AppendAsync(Tenant, Grant(), "source-a");
        var reason = new GrantReason(GrantReasonCodes.RevocationCompromise, "incident-7");
        var revoked = await h.GrantStore.RevokeAsync(Tenant, grant.GrantId,
            new GrantRevocation(Admin, Now, reason));
        Assert.Equal(GrantStatus.Revoked, revoked!.Status);
        Assert.Equal(reason, revoked.Revocation!.Reason);
        Assert.NotNull(await h.GrantStore.FindAsync(Tenant, grant.GrantId));
    }

    [Fact]
    public async Task GrantStore_RevokedGrantIsImmutableAndRevocationReplayRequiresIdenticalEvidence()
    {
        await using var h = await VecTestHarness.CreateAsync();
        var grant = await h.GrantStore.AppendAsync(Tenant, Grant(), "source-a");
        var revocation = new GrantRevocation(
            Admin, Now, new GrantReason(GrantReasonCodes.RevocationCompromise, "incident-7"));
        var revoked = await h.GrantStore.RevokeAsync(Tenant, grant.GrantId, revocation);

        var replay = await h.GrantStore.RevokeAsync(Tenant, grant.GrantId, revocation);
        Assert.Equal(revoked, replay);
        await using (var db = h.Store.CreateContext())
            Assert.Equal(2, db.Grants.Single().OwnerVersion);

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.GrantStore.ChangeValidityAsync(
            Tenant, grant.GrantId, new GrantValidity(Now, Now.AddDays(1)), Admin,
            new GrantReason(GrantReasonCodes.Manual)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.GrantStore.RecordReviewAsync(
            Tenant, grant.GrantId, Now.AddHours(1), Admin));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.GrantStore.RevokeAsync(
            Tenant, grant.GrantId, revocation with { RevokedBy = new ActorId("other-admin") }));
    }

    [Fact]
    public void GrantStore_HasNoDeleteOrRemoveOperation()
    {
        var names = typeof(IGrantStore).GetMethods().Select(method => method.Name).ToArray();
        Assert.DoesNotContain("DeleteAsync", names);
        Assert.DoesNotContain("RemoveAsync", names);
    }

    private static AccessGrant Grant() => TestSearchAuthorization.Grant(
        Tenant, User, ScopeExpression.Parse("/"), Now);
}
